using System.Buffers.Binary;

/// <summary>
/// The one binary form a <see cref="ScryDisclosureBatch"/> is kept in, by whichever sink keeps it: a
/// journal file, an outbox column. Versioned by its first four bytes, so a batch written by a newer
/// version is refused by name rather than misread.
/// </summary>
/// <remarks>
/// Little-endian integers, UTF-8 strings behind a length, addresses as their thirty-two bytes. An
/// entity names its source and its route through a table of the batch's own, since a response's rows
/// repeat the same few strings once each.
/// </remarks>
static class DisclosureBatchCodec
{
    // "SDB" and the version.
    const uint magic = 0x01424453;

    const byte hasBegin = 1;
    const byte hasShape = 2;
    const byte hasClose = 4;
    const byte hasReview = 8;

    public static void Write(ScryDisclosureBatch batch, IBufferWriter<byte> output)
    {
        var writer = new Writer(output);
        writer.UInt32(magic);
        writer.Guid(batch.EventId);
        writer.Int32(batch.Sequence);

        byte flags = 0;
        if (batch.Begin is not null)
        {
            flags |= hasBegin;
        }

        if (batch.Shape is not null)
        {
            flags |= hasShape;
        }

        if (batch.Close is not null)
        {
            flags |= hasClose;
        }

        if (batch.Review is not null)
        {
            flags |= hasReview;
        }

        writer.Byte(flags);

        if (batch.Begin is { } begin)
        {
            writer.Guid(begin.Id);
            writer.Time(begin.At);
            writer.Byte((byte) begin.Kind);
            writer.String(begin.Source);
            writer.String(begin.Caller);
            writer.Bool(begin.Subscribed);
            writer.Byte((byte) begin.Delivery);
            writer.Address(begin.Request);
            writer.Address(begin.Shape);
            writer.Bool(begin.Sensitive);
            writer.String(begin.Stamp);
            writer.String(begin.Correlation);
            writer.String(begin.Node);
            writer.String(begin.ContentType);
        }

        if (batch.Shape is { } shape)
        {
            writer.Address(shape.Address);
            writer.Int32(shape.Fields.Count);
            foreach (var field in shape.Fields)
            {
                writer.String(field.Source);
                writer.String(field.Member);
                writer.Byte((byte) field.Use);
                writer.Bool(field.Sensitive);
            }
        }

        writer.Int32(batch.Units.Count);
        foreach (var unit in batch.Units)
        {
            writer.Int32(unit.Ordinal);
            writer.Address(unit.Content);
        }

        // The sources and routes the entities name, once each, then every entity by position in it.
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in batch.Entities)
        {
            table.TryAdd(entity.Source, table.Count);
            table.TryAdd(entity.Via, table.Count);
        }

        writer.Int32(table.Count);
        foreach (var name in table.Keys)
        {
            writer.String(name);
        }

        writer.Int32(batch.Entities.Count);
        foreach (var entity in batch.Entities)
        {
            writer.Int32(entity.Ordinal);
            writer.Int32(entity.Slot);
            writer.Int32(table[entity.Source]);
            writer.String(entity.Key);
            writer.Int32(table[entity.Via]);
        }

        writer.Int32(batch.Contents.Count);
        foreach (var content in batch.Contents)
        {
            writer.Address(content.Address);
            writer.Byte((byte) content.Kind);
            writer.Int32(content.Length);
            writer.Bool(content.Held);
            if (content.Held)
            {
                writer.Bytes(content.Bytes.Span);
            }
        }

        if (batch.Close is { } close)
        {
            writer.Byte((byte) close.Outcome);
            writer.Int32(close.Units);
            writer.Time(close.At);
            writer.Address(close.Response);
        }

        if (batch.Review is { } review)
        {
            writer.Guid(review.Id);
            writer.Time(review.At);
            writer.Byte((byte) review.Question);
            writer.String(review.Reviewer);
            writer.Address(review.Parameters);
            writer.Int32(review.Results);
            writer.Int32(review.Events.Count);
            foreach (var id in review.Events)
            {
                writer.Guid(id);
            }

            writer.String(review.Node);
        }
    }

    public static ScryDisclosureBatch Read(ReadOnlySpan<byte> bytes)
    {
        var reader = new Reader(bytes);
        if (reader.UInt32() != magic)
        {
            throw new FormatException("These bytes are not a disclosure batch this version of Scry reads.");
        }

        var eventId = reader.Guid();
        var sequence = reader.Int32();
        var flags = reader.Byte();

        ScryDisclosureEvent? begin = null;
        if ((flags & hasBegin) != 0)
        {
            var id = reader.Guid();
            var at = reader.Time();
            var kind = (ScryDisclosureKind) reader.Byte();
            var source = reader.Text();
            begin = new(id, at, kind, source)
            {
                Caller = reader.String(),
                Subscribed = reader.Bool(),
                Delivery = (ScryDisclosureDelivery) reader.Byte(),
                Request = reader.OptionalAddress(),
                Shape = reader.OptionalAddress(),
                Sensitive = reader.Bool(),
                Stamp = reader.String(),
                Correlation = reader.String(),
                Node = reader.String(),
                ContentType = reader.String()
            };
        }

        ScryDisclosureShape? shape = null;
        if ((flags & hasShape) != 0)
        {
            var address = reader.Address();
            var fields = new ScryDisclosureField[reader.Count()];
            for (var index = 0; index < fields.Length; index++)
            {
                fields[index] = new(reader.Text(), reader.Text(), (ScryDisclosureFieldUse) reader.Byte(), reader.Bool());
            }

            shape = new(address, fields);
        }

        var units = new ScryDisclosureUnit[reader.Count()];
        for (var index = 0; index < units.Length; index++)
        {
            units[index] = new(reader.Int32(), reader.Address());
        }

        var table = new string[reader.Count()];
        for (var index = 0; index < table.Length; index++)
        {
            table[index] = reader.Text();
        }

        var entities = new ScryDisclosureEntity[reader.Count()];
        for (var index = 0; index < entities.Length; index++)
        {
            var ordinal = reader.Int32();
            var slot = reader.Int32();
            var source = reader.Named(table);
            var key = reader.Text();
            entities[index] = new(ordinal, slot, source, key, reader.Named(table));
        }

        var contents = new ScryDisclosureContent[reader.Count()];
        for (var index = 0; index < contents.Length; index++)
        {
            var address = reader.Address();
            var kind = (ScryDisclosureContentKind) reader.Byte();
            var length = reader.Int32();
            ReadOnlyMemory<byte> held = default;
            if (reader.Bool())
            {
                held = reader.Bytes();
            }

            contents[index] = new(address, kind, length, held);
        }

        ScryDisclosureClose? close = null;
        if ((flags & hasClose) != 0)
        {
            var outcome = (ScryDisclosureOutcome) reader.Byte();
            close = new(outcome, reader.Int32())
            {
                At = reader.Time(),
                Response = reader.OptionalAddress()
            };
        }

        ScryDisclosureReview? review = null;
        if ((flags & hasReview) != 0)
        {
            var id = reader.Guid();
            var at = reader.Time();
            var question = (ScryDisclosureQuestion) reader.Byte();
            var reviewer = reader.String();
            var parameters = reader.OptionalAddress();
            var results = reader.Int32();
            var events = new Guid[reader.Count()];
            for (var index = 0; index < events.Length; index++)
            {
                events[index] = reader.Guid();
            }

            review = new(id, at, question)
            {
                Reviewer = reviewer,
                Parameters = parameters,
                Results = results,
                Events = events,
                Node = reader.String()
            };
        }

        return new()
        {
            EventId = eventId,
            Sequence = sequence,
            Begin = begin,
            Shape = shape,
            Units = units,
            Entities = entities,
            Contents = contents,
            Close = close,
            Review = review
        };
    }

    ref struct Writer(IBufferWriter<byte> output)
    {
        public void Byte(byte value)
        {
            output.GetSpan(1)[0] = value;
            output.Advance(1);
        }

        public void Bool(bool value) =>
            Byte(Convert.ToByte(value));

        public void Int32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.GetSpan(4), value);
            output.Advance(4);
        }

        public void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(4), value);
            output.Advance(4);
        }

        public void Time(DateTimeOffset value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(output.GetSpan(8), value.UtcTicks);
            output.Advance(8);
        }

        public void Guid(Guid value)
        {
            value.TryWriteBytes(output.GetSpan(16));
            output.Advance(16);
        }

        public void Address(ScryDisclosureAddress value)
        {
            value.CopyTo(output.GetSpan(ScryDisclosureAddress.Size));
            output.Advance(ScryDisclosureAddress.Size);
        }

        public void Address(ScryDisclosureAddress? value)
        {
            Bool(value is not null);
            if (value is { } address)
            {
                Address(address);
            }
        }

        public void Bytes(ReadOnlySpan<byte> value)
        {
            Int32(value.Length);
            value.CopyTo(output.GetSpan(value.Length));
            output.Advance(value.Length);
        }

        // Null is a length of minus one, so an absent caller reads back absent rather than empty.
        public void String(string? value)
        {
            if (value is null)
            {
                Int32(-1);
                return;
            }

            var length = Encoding.UTF8.GetByteCount(value);
            Int32(length);
            Encoding.UTF8.GetBytes(value, output.GetSpan(length));
            output.Advance(length);
        }
    }

    ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> rest = bytes;

        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 ||
                count > rest.Length)
            {
                throw new FormatException("A disclosure batch ended before what it declared.");
            }

            var taken = rest[..count];
            rest = rest[count..];
            return taken;
        }

        public byte Byte() =>
            Take(1)[0];

        public bool Bool() =>
            Byte() != 0;

        public int Int32() =>
            BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint UInt32() =>
            BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        // A count of things each at least a byte long, so one larger than what is left is a lie
        // about the batch rather than an array to allocate.
        public int Count()
        {
            var count = Int32();
            if (count < 0 ||
                count > rest.Length)
            {
                throw new FormatException("A disclosure batch declared more than it holds.");
            }

            return count;
        }

        public DateTimeOffset Time() =>
            new(BinaryPrimitives.ReadInt64LittleEndian(Take(8)), TimeSpan.Zero);

        public Guid Guid() =>
            new(Take(16));

        public ScryDisclosureAddress Address() =>
            ScryDisclosureAddress.From(Take(ScryDisclosureAddress.Size));

        public ScryDisclosureAddress? OptionalAddress()
        {
            if (Bool())
            {
                return Address();
            }

            return null;
        }

        public byte[] Bytes() =>
            Take(Int32()).ToArray();

        public string? String()
        {
            var length = Int32();
            if (length == -1)
            {
                return null;
            }

            return Encoding.UTF8.GetString(Take(length));
        }

        public string Text() =>
            String() ?? throw new FormatException("A disclosure batch left out a name it has to carry.");

        public string Named(string[] table)
        {
            var index = Int32();
            if ((uint) index >= (uint) table.Length)
            {
                throw new FormatException("A disclosure batch named a string it does not carry.");
            }

            return table[index];
        }
    }
}
