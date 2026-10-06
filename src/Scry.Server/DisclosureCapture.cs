/// <summary>
/// What one call is about to hand its caller, gathered as it is written: an address per unit, the
/// content behind each address this event has not already carried, and the rows they were read from.
/// Handed to the sink before any of it is released — once at the end for a response held whole, and
/// before every drain for one that is sent as it is written.
/// </summary>
/// <remarks>
/// <para>
/// A row is written once. It goes into a scratch buffer behind its kind byte, is hashed where it
/// lies, and is copied from there into the response — so recording a row costs a hash and two copies
/// of its bytes, and no second serialization. A row holding a binary value is the exception: its
/// canonical form names the value by digest where the wire carries the bytes, so it is written twice.
/// </para>
/// <para>
/// One per call, used from that call alone, and disposed when it ends. Disposal settles what the
/// record is owed: a close for an event that began and never finished, and a withdrawal for one that
/// was accepted whole and then not handed on.
/// </para>
/// </remarks>
sealed class DisclosureCapture(
    DisclosureRuntime runtime,
    IServiceProvider services,
    object? request,
    string source,
    string? caller) :
    IDisposable,
    IAsyncDisposable
{
    static JsonEncodedText bytesProperty = JsonEncodedText.Encode("$bytes");
    static JsonEncodedText lengthProperty = JsonEncodedText.Encode("length");

    PooledBufferWriter scratch = new();
    Utf8JsonWriter? scratchJson;

    // The content gathered since the last accept, end to end in one buffer and named by where each
    // piece lies in it: the buffer moves as it grows, so a slice taken now would not survive.
    PooledBufferWriter arena = new();
    List<(ScryDisclosureAddress Address, ScryDisclosureContentKind Kind, int Offset, int Length, bool Held)> contents = [];
    List<ScryDisclosureUnit> units = [];
    List<ScryDisclosureEntity> entities = [];

    // Every address this event has carried content for. An answer repeating a row carries it once.
    HashSet<ScryDisclosureAddress> carried = [];

    // Over the unit addresses in order, which is what makes two identical answers recognisable.
    IncrementalHash response = runtime.Incremental();
    IncrementalHash? tagging;

    DateTimeOffset at = runtime.Now;
    ScryDisclosureKind kind;
    ScryDisclosureShape? shape;
    bool sensitive;
    int ordinal;

    // How many units the sink holds, and how many of those it held before the close: the second is
    // what had already left when an answer that was accepted whole turned out not to be handed on.
    int accepted;
    int drained;
    int sequence;
    bool begun;
    bool closed;
    bool released;
    bool settled;

    // Set where the sink was handed a batch and did not answer that it had it. Not answering is not
    // the same as not having it: a store can commit and then time out saying so.
    bool unanswered;
    int? handed;
    ScryDisclosureOutcome abandoned = ScryDisclosureOutcome.Failed;

    /// <summary>The event's id: what an audit entry names to say this answer was recorded.</summary>
    public Guid Id { get; } = Guid.CreateVersion7();

    /// <summary>What ties this event to others made for the same request.</summary>
    public string? Correlation { get; set; }

    /// <summary>Whether this is one answer of a live query.</summary>
    public bool Subscribed { get; set; }

    /// <summary>
    /// Whether the content crossed to the caller, or the caller named what it already held and was
    /// told it still stands.
    /// </summary>
    public ScryDisclosureDelivery Delivery { get; set; }

    /// <summary>The media type an attachment's bytes were sent as.</summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Says how many units have been handed on, for an answer that is handed on a unit at a time. Where
    /// it is said, a close owed at disposal counts exactly these; where it is not, it counts everything
    /// the sink accepted, which is the most that can have gone.
    /// </summary>
    public void Handed(int count) =>
        handed = count;

    /// <summary>Says what kind of answer is being written, before its first unit.</summary>
    public void Begin(ScryDisclosureKind answer) =>
        kind = answer;

    /// <summary>
    /// Records one row and writes it into the response: the same bytes, written once, unless the row
    /// carries a binary value.
    /// </summary>
    public void WriteRow(Utf8JsonWriter json, ProjectionPlan plan, object[] row, BinaryPartCollector? binary)
    {
        var writer = plan.Writer;

        // Whether the plan has a slot that could hold a byte[], which is the one thing that makes a
        // row's record differ from what is sent.
        var bytes = plan.HasBytes;
        Identify(plan, row);
        var canonical = Canonical(writer, row, bytes);
        Unit(ScryDisclosureContentKind.Row, canonical);

        // The canonical row names a binary value by its digest, where the wire carries the value or a
        // part's index. So the wire's form is written apart, and is the only one to touch the collector.
        if (bytes)
        {
            writer.WriteRow(json, row, binary);
            return;
        }

        json.WriteRawValue(canonical[1..], skipInputValidation: true);
    }

    /// <summary>
    /// Records one row that something else is writing: the path that shapes rows into objects, and a
    /// stream holding rows back. Returns how many bytes the row's record came to.
    /// </summary>
    public int AddRow(ProjectionPlan plan, object[] row)
    {
        Identify(plan, row);
        var canonical = Canonical(plan.Writer, row, plan.HasBytes);
        Unit(ScryDisclosureContentKind.Row, canonical);
        return canonical.Length - 1;
    }

    /// <summary>
    /// Records one row from the bytes it was already written as: a row with no binary value, whose
    /// record is what is sent. So a row written ahead of being recorded — to learn its length before it
    /// is committed to — is hashed where it lies rather than written again.
    /// </summary>
    public void AddWritten(ProjectionPlan plan, object[] row, ReadOnlySpan<byte> written)
    {
        Identify(plan, row);
        var address = Tagged(ScryDisclosureContentKind.Row, written);
        Hold(address, ScryDisclosureContentKind.Row, written);
        Count(address);
    }

    // Says which rows the unit about to be recorded was read from: each key the selector read beside
    // what was asked for, in the slots past the ones that are written. A key with a null in it is a
    // row that was not there — a navigation that led nowhere, a row a policy hid, the unmatched side
    // of an outer join — and is recorded as nothing.
    void Identify(ProjectionPlan plan, object[] row)
    {
        if (plan.Entities is not { } slots)
        {
            return;
        }

        for (var slot = 0; slot < slots.Count; slot++)
        {
            var entity = slots[slot];
            if (Key(row, entity) is { } key)
            {
                entities.Add(new(ordinal, slot, entity.Source, key, entity.Via));
            }
        }
    }

    // A key as the record holds one: the JSON array of its values. Written through the scratch
    // buffer, which the row that follows takes over.
    string? Key(object[] row, DisclosureSlot slot)
    {
        for (var index = 0; index < slot.Count; index++)
        {
            if (row[slot.First + index] is null)
            {
                return null;
            }
        }

        scratch.Reset();
        var json = Json();
        json.WriteStartArray();
        for (var index = 0; index < slot.Count; index++)
        {
            PlanShapeWriter.WriteValue(json, row[slot.First + index]);
        }

        json.WriteEndArray();
        json.Flush();
        return Encoding.UTF8.GetString(scratch.WrittenMemory.Span);
    }

    /// <summary>
    /// Takes what the query was found to read while it was rebound: the members, each with what was
    /// done with it, as the shape every answer of this query has.
    /// </summary>
    public void Describe(DisclosurePlanner planner) =>
        Describe(planner.Fields(), planner.Sensitive);

    /// <summary>The same, for an answer whose members are known without a query to rebind.</summary>
    public void Describe(IReadOnlyList<ScryDisclosureField> read, bool marked)
    {
        if (read.Count == 0)
        {
            return;
        }

        // Addressed like content, by what it says: the same query read the same members whoever
        // asked it, so every answer of it names one shape.
        var json = Scratch(ScryDisclosureContentKind.Shape);
        json.WriteStartArray();
        foreach (var field in read)
        {
            json.WriteStartArray();
            json.WriteStringValue(field.Source);
            json.WriteStringValue(field.Member);
            json.WriteNumberValue((byte) field.Use);
            json.WriteBooleanValue(field.Sensitive);
            json.WriteEndArray();
        }

        json.WriteEndArray();
        json.Flush();
        shape = new(runtime.Address(scratch.WrittenMemory.Span), read);
        sensitive = marked;
    }

    /// <summary>Records a scalar answer and writes it into the response.</summary>
    public void WriteScalar(Utf8JsonWriter json, object? value)
    {
        var canonical = Canonical(value);
        Unit(ScryDisclosureContentKind.Scalar, canonical);
        if (value is byte[])
        {
            PlanShapeWriter.WriteValue(json, value);
            return;
        }

        json.WriteRawValue(canonical[1..], skipInputValidation: true);
    }

    /// <summary>Records a scalar answer that something else is writing.</summary>
    public void AddScalar(object? value) =>
        Unit(ScryDisclosureContentKind.Scalar, Canonical(value));

    /// <summary>
    /// Records a unit whose content is already bytes of its own: a receipt, a schema, SQL text. Kept
    /// whole.
    /// </summary>
    public void AddUnit(ScryDisclosureContentKind content, ReadOnlySpan<byte> bytes)
    {
        var address = Tagged(content, bytes);
        Hold(address, content, bytes);
        Count(address);
    }

    /// <summary>
    /// Records a unit that is a binary value, by its digest — and its bytes too, where the host asked
    /// for binary content to be kept.
    /// </summary>
    public ScryDisclosureAddress AddBinary(ReadOnlySpan<byte> bytes)
    {
        var address = Binary(bytes);
        Count(address);
        return address;
    }

    /// <summary>Says which row of which source the unit about to be recorded was read from.</summary>
    public void AddEntity(int slot, string entitySource, string key, string via) =>
        entities.Add(new(ordinal, slot, entitySource, key, via));

    /// <summary>
    /// A binary value's place in a canonical row: its address and its length, where the wire has the
    /// bytes. Called by the shape writer's canonical mode, part-way through a row.
    /// </summary>
    public void WriteDigest(Utf8JsonWriter json, byte[] bytes)
    {
        var address = Binary(bytes);
        json.WriteStartObject();
        json.WriteString(bytesProperty, address.ToString());
        json.WriteNumber(lengthProperty, bytes.Length);
        json.WriteEndObject();
    }

    ScryDisclosureAddress Binary(ReadOnlySpan<byte> bytes)
    {
        var address = Tagged(ScryDisclosureContentKind.Bytes, bytes);
        if (runtime.Settings.StoreBinaryContent)
        {
            Hold(address, ScryDisclosureContentKind.Bytes, bytes);
        }
        else if (carried.Add(address))
        {
            contents.Add((address, ScryDisclosureContentKind.Bytes, 0, bytes.Length, false));
        }

        return address;
    }

    ReadOnlySpan<byte> Canonical(PlanShapeWriter writer, object[] row, bool bytes)
    {
        var json = Scratch(ScryDisclosureContentKind.Row);
        if (bytes)
        {
            writer.WriteCanonicalRow(json, row, this);
        }
        else
        {
            writer.WriteRow(json, row);
        }

        json.Flush();
        return scratch.WrittenMemory.Span;
    }

    ReadOnlySpan<byte> Canonical(object? value)
    {
        var json = Scratch(ScryDisclosureContentKind.Scalar);
        if (value is byte[] bytes)
        {
            WriteDigest(json, bytes);
        }
        else
        {
            PlanShapeWriter.WriteValue(json, value);
        }

        json.Flush();
        return scratch.WrittenMemory.Span;
    }

    // The scratch buffer, emptied and holding the kind byte the content's address is taken over.
    Utf8JsonWriter Scratch(ScryDisclosureContentKind content)
    {
        scratch.Reset();
        scratch.GetSpan(1)[0] = (byte) content;
        scratch.Advance(1);
        return Json();
    }

    // The one writer over the scratch buffer, begun again where the buffer now ends.
    Utf8JsonWriter Json()
    {
        if (scratchJson is null)
        {
            scratchJson = new(scratch);
        }
        else
        {
            scratchJson.Reset(scratch);
        }

        return scratchJson;
    }

    void Unit(ScryDisclosureContentKind content, ReadOnlySpan<byte> tagged)
    {
        var address = runtime.Address(tagged);
        Hold(address, content, tagged[1..]);
        Count(address);
    }

    void Count(ScryDisclosureAddress address)
    {
        units.Add(new(ordinal++, address));
        Span<byte> bytes = stackalloc byte[ScryDisclosureAddress.Size];
        address.CopyTo(bytes);
        response.AppendData(bytes);
    }

    // The address of content that is not lying behind its kind byte, fed to the hash in two pieces
    // rather than copied to put it there.
    ScryDisclosureAddress Tagged(ScryDisclosureContentKind content, ReadOnlySpan<byte> bytes)
    {
        tagging ??= runtime.Incremental();
        ReadOnlySpan<byte> tag = [(byte) content];
        tagging.AppendData(tag);
        tagging.AppendData(bytes);
        Span<byte> hash = stackalloc byte[ScryDisclosureAddress.Size];
        tagging.GetHashAndReset(hash);
        return ScryDisclosureAddress.From(hash);
    }

    void Hold(ScryDisclosureAddress address, ScryDisclosureContentKind content, ReadOnlySpan<byte> bytes)
    {
        if (!carried.Add(address))
        {
            return;
        }

        var offset = arena.WrittenCount;
        bytes.CopyTo(arena.GetSpan(bytes.Length));
        arena.Advance(bytes.Length);
        contents.Add((address, content, offset, bytes.Length, true));
    }

    /// <summary>
    /// Hands what has been gathered to the sink, awaiting its acceptance. Called before bytes are
    /// handed on ahead of the end: what the sink has accepted is what may now go.
    /// </summary>
    public async ValueTask FlushAsync(Cancel cancel)
    {
        if (units.Count == 0 &&
            begun)
        {
            return;
        }

        await HandAsync(Batch(close: null), cancel);
        Accepted();
        drained = ordinal;
    }

    /// <summary>The same, blocking.</summary>
    public void Flush()
    {
        if (units.Count == 0 &&
            begun)
        {
            return;
        }

        Hand(Batch(close: null));
        Accepted();
        drained = ordinal;
    }

    /// <summary>
    /// Hands the rest to the sink with the event's close, awaiting its acceptance. The answer may be
    /// handed on once this returns, and not before.
    /// </summary>
    public async ValueTask CommitAsync(Cancel cancel)
    {
        await HandAsync(Batch(Whole()), cancel);
        Accepted();
        closed = true;
    }

    /// <summary>The same, blocking.</summary>
    public void Commit()
    {
        Hand(Batch(Whole()));
        Accepted();
        closed = true;
    }

    // Hands a batch to the sink. One that fails may have been kept all the same, so the event is
    // owed a close saying nothing of that batch went, whether or not the sink has its beginning.
    async ValueTask HandAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        try
        {
            await runtime.AppendAsync(batch, services, cancel);
        }
        catch
        {
            unanswered = true;
            sequence++;
            throw;
        }
    }

    void Hand(ScryDisclosureBatch batch)
    {
        try
        {
            runtime.Append(batch, services);
        }
        catch
        {
            unanswered = true;
            sequence++;
            throw;
        }
    }

    /// <summary>
    /// Says the answer was handed on: the last thing a call does with its capture before returning
    /// what it wrote. A capture disposed without it, after its commit, withdraws the event.
    /// </summary>
    public void Released() =>
        released = true;

    /// <summary>Says the call was abandoned by its caller, for the close a disposal may yet owe.</summary>
    public void Abandoned() =>
        abandoned = ScryDisclosureOutcome.Canceled;

    // The close of an answer that was written to its end: every unit, and the address they add up to.
    ScryDisclosureClose Whole()
    {
        Span<byte> hash = stackalloc byte[ScryDisclosureAddress.Size];
        response.GetHashAndReset(hash);
        return new(ScryDisclosureOutcome.Released, ordinal)
        {
            At = runtime.Now,
            Response = ScryDisclosureAddress.From(hash)
        };
    }

    ScryDisclosureBatch Batch(ScryDisclosureClose? close)
    {
        ScryDisclosureEvent? header = null;
        if (!begun)
        {
            header = Header();
        }

        var held = arena.WrittenMemory;
        var batch = new ScryDisclosureContent[contents.Count];
        for (var index = 0; index < batch.Length; index++)
        {
            var content = contents[index];
            ReadOnlyMemory<byte> bytes = default;
            if (content.Held)
            {
                bytes = held.Slice(content.Offset, content.Length);
            }

            batch[index] = new(content.Address, content.Kind, content.Length, bytes);
        }

        // The shape goes with the event's beginning, once: it is the event's, not a batch's.
        ScryDisclosureShape? described = null;
        if (header is not null)
        {
            described = shape;
        }

        return new()
        {
            EventId = Id,
            Sequence = sequence,
            Begin = header,
            Shape = described,
            Units = [.. units],
            Entities = [.. entities],
            Contents = batch,
            Close = close
        };
    }

    // Made as the first batch is, so the request is serialized once and only for an answer that got
    // as far as having something to record.
    ScryDisclosureEvent Header()
    {
        ScryDisclosureAddress? asked = null;
        if (Asked() is { } bytes)
        {
            var address = Tagged(ScryDisclosureContentKind.Request, bytes);
            Hold(address, ScryDisclosureContentKind.Request, bytes);
            asked = address;
        }

        return new(Id, at, kind, source)
        {
            Caller = caller,
            Subscribed = Subscribed,
            Delivery = Delivery,
            ContentType = ContentType,
            Request = asked,
            Shape = shape?.Address,
            Sensitive = sensitive,
            Stamp = runtime.Stamp,
            Correlation = Correlation,
            Node = runtime.Settings.Node
        };
    }

    // What was asked, as the bytes it is kept as, or null where nothing was: a schema read. Each
    // kind of request is written by the wire's own serializer, so what is kept is what was sent.
    byte[]? Asked() =>
        request switch
        {
            QueryRequest query => ScryJson.SerializeToUtf8(query),
            AttachmentRequest attachment => ScryJson.SerializeToUtf8(attachment),
            CommandRequest command => ScryJson.SerializeToUtf8(command),
            _ => null
        };

    /// <summary>
    /// Ends an answer that was handed on a unit at a time, saying how it ended and how many units
    /// went. What was gathered and never accepted is let go: it was never handed on.
    /// </summary>
    public async ValueTask EndAsync(ScryDisclosureOutcome outcome, int count, Cancel cancel)
    {
        units.Clear();
        entities.Clear();
        contents.Clear();
        arena.Reset();
        await HandAsync(Batch(Ended(outcome, count)), cancel);
        Accepted();
        closed = true;
        released = true;
    }

    /// <summary>
    /// Records that the caller was refused because a row it may not read matched: nothing was sent,
    /// and the caller learned that such a row exists. Whatever had been gathered is let go.
    /// </summary>
    public async ValueTask DenyAsync(Cancel cancel)
    {
        Denying();
        await HandAsync(Batch(Ended(ScryDisclosureOutcome.Released, 0)), cancel);
        Accepted();
        closed = true;
        released = true;
    }

    /// <summary>The same, blocking.</summary>
    public void Deny()
    {
        Denying();
        Hand(Batch(Ended(ScryDisclosureOutcome.Released, 0)));
        Accepted();
        closed = true;
        released = true;
    }

    void Denying()
    {
        kind = ScryDisclosureKind.Denial;
        ordinal = accepted;
        units.Clear();
        entities.Clear();
        contents.Clear();
        arena.Reset();
    }

    ScryDisclosureClose Ended(ScryDisclosureOutcome outcome, int count) =>
        new(outcome, count)
        {
            At = runtime.Now
        };

    void Accepted()
    {
        begun = true;
        sequence++;
        accepted = ordinal;
        units.Clear();
        entities.Clear();
        contents.Clear();
        arena.Reset();
    }

    // What the record is still owed when the call ends, or null where it is owed nothing.
    //
    // Begun and never closed: something failed after part of the answer was accepted, and perhaps
    // handed on. The units the sink accepted are the most that can have gone.
    //
    // Closed and never released: the whole answer was accepted and a later step failed — an auditor
    // threw. What had already left stands, and nothing else went; where nothing had, the event is
    // withdrawn. A second close, which is why the last one written is the one that stands.
    //
    // Handed over and not answered for: the sink failed, or the caller left while it was being
    // asked. It may hold the batch regardless, so it is told how far the answer really got. Where it
    // holds nothing of the event, a close is all it is left with, and that describes nothing.
    ScryDisclosureBatch? Owed()
    {
        if (settled ||
            released ||
            !(begun || unanswered))
        {
            return null;
        }

        settled = true;
        return new()
        {
            EventId = Id,
            Sequence = sequence++,
            Close = new(Outcome(), Units())
            {
                At = runtime.Now
            }
        };
    }

    ScryDisclosureOutcome Outcome()
    {
        if (!closed)
        {
            return abandoned;
        }

        // Closed as whole and then not handed on whole. Where it went a unit at a time, how it
        // stopped is what the call said; where it went in one piece, some of it had left or none had.
        if (handed is not null)
        {
            return abandoned;
        }

        if (drained > 0)
        {
            return ScryDisclosureOutcome.Truncated;
        }

        return ScryDisclosureOutcome.Retracted;
    }

    int Units()
    {
        // Counted as it went, where the answer was handed on a unit at a time.
        if (handed is { } count)
        {
            return count;
        }

        if (closed)
        {
            return drained;
        }

        return accepted;
    }

    public void Dispose()
    {
        if (Owed() is { } owed)
        {
            try
            {
                runtime.Append(owed, services);
            }
            catch (Exception)
            {
                // The failure being reported is the one that ended the call. An event left without its
                // close reads as "may have been sent", which over-reports and never under-reports.
            }
        }

        Free();
    }

    public async ValueTask DisposeAsync()
    {
        if (Owed() is { } owed)
        {
            try
            {
                // Not the request's token: the caller having gone is the commonest reason there is a
                // close to write at all.
                await runtime.AppendAsync(owed, services, Cancel.None);
            }
            catch (Exception)
            {
                // As above.
            }
        }

        Free();
    }

    void Free()
    {
        scratchJson?.Dispose();
        scratchJson = null;
        scratch.Dispose();
        arena.Dispose();
        response.Dispose();
        tagging?.Dispose();
        tagging = null;
    }
}
