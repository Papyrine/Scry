namespace Scry;

/// <summary>
/// Records what host code sends outside Scry — a command handler's email, a job's export, a message
/// to another system — in the record every Scry answer is kept in, before it is sent.
/// </summary>
/// <remarks>
/// <para>
/// The audit's guarantee, that nothing leaves before its record is accepted, is kept here by the
/// host: await <see cref="RecordAsync"/>, then send, and send nothing where it throws. Where the send
/// then fails, <see cref="WithdrawAsync"/> says how much of it went, as a second close; left unsaid,
/// the record says all of it went, which over-reports and never under-reports.
/// </para>
/// <para>
/// Needs a sink and the audit's settings, and nothing of a model: so a worker that handles commands
/// and serves no queries records to the same store as the servers that do. <c>AddScry</c> registers
/// one where the audit is on, and <see cref="ScryServiceExtensions.AddScryDisclosureRecord"/> where
/// the host has no model. Of the settings, <see cref="ScryDisclosureOptions.AddressKey"/>,
/// <see cref="ScryDisclosureOptions.Node"/> and <see cref="ScryDisclosureOptions.Clock"/> are read.
/// </para>
/// <para>
/// Nothing here checks a source's name or a key's values against a model, since there may be none.
/// A row named otherwise than a query of it would be recorded under is not found as the same row.
/// </para>
/// </remarks>
public sealed class ScryExternalDisclosures(IScryDisclosureSink sink, ScryDisclosureOptions settings)
{
    /// <summary>The settings disclosures are recorded under.</summary>
    public ScryDisclosureOptions Settings => settings;

    /// <summary>Records one external disclosure, as sent in full, and returns it as recorded.</summary>
    /// <param name="disclosure">What is about to be sent, and to whom.</param>
    /// <param name="cancel">Ends the wait on the sink.</param>
    /// <exception cref="ArgumentException">The disclosure names no recipient, or a row with no source or no key.</exception>
    /// <exception cref="ScryDisclosureException">The sink did not accept the record. Nothing is then to be sent.</exception>
    public async ValueTask<ScryExternalRecord> RecordAsync(ScryExternalDisclosure disclosure, Cancel cancel = default)
    {
        var (record, batch) = Make(settings, disclosure);
        await DisclosureRuntime.AppendAsync(sink, batch, cancel);
        return record;
    }

    /// <summary>
    /// Says that a disclosure recorded by <see cref="RecordAsync"/> did not go in full: the send failed
    /// or was stopped after <paramref name="sent"/> of its units had gone. Zero withdraws it. Said
    /// once: a second call for the same record changes nothing.
    /// </summary>
    /// <param name="record">What <see cref="RecordAsync"/> returned.</param>
    /// <param name="sent">How many units went, counted from the first.</param>
    /// <param name="cancel">Ends the wait on the sink.</param>
    /// <exception cref="ScryDisclosureException">
    /// The sink did not accept it. The record then still says all of it went.
    /// </exception>
    public async ValueTask WithdrawAsync(ScryExternalRecord record, int sent = 0, Cancel cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sent);
        var outcome = ScryDisclosureOutcome.Truncated;
        if (sent == 0)
        {
            outcome = ScryDisclosureOutcome.Retracted;
        }

        // The second batch of the event: a second close, which stands over the first because it was
        // written last. A second withdrawal is the same batch by id and sequence, which a store keeps
        // once: the first withdrawal stands.
        var batch = new ScryDisclosureBatch
        {
            EventId = record.Id,
            Sequence = 1,
            Close = new(outcome, sent)
            {
                At = settings.Clock.GetUtcNow()
            }
        };
        await DisclosureRuntime.AppendAsync(sink, batch, cancel);
    }

    // The record and the one batch that holds it: the event, every unit, the rows each was made from,
    // and a close saying it was released, so it reads as an answer sent whole does.
    static (ScryExternalRecord Record, ScryDisclosureBatch Batch) Make(ScryDisclosureOptions settings, ScryExternalDisclosure disclosure)
    {
        if (string.IsNullOrWhiteSpace(disclosure.Recipient))
        {
            throw new ArgumentException("An external disclosure has to name who it is sent to: that is what the record exists to answer.", nameof(disclosure));
        }

        var key = settings.AddressKey;
        var units = new List<ScryDisclosureUnit>(disclosure.Units.Count);
        var entities = new List<ScryDisclosureEntity>();
        var contents = new List<ScryDisclosureContent>();
        var carried = new HashSet<ScryDisclosureAddress>();
        using var response = DisclosureRuntime.Incremental(key);
        Span<byte> addressBytes = stackalloc byte[ScryDisclosureAddress.Size];
        for (var ordinal = 0; ordinal < disclosure.Units.Count; ordinal++)
        {
            var unit = disclosure.Units[ordinal];
            var address = Address(key, unit.Content.Span);
            units.Add(new(ordinal, address));
            address.CopyTo(addressBytes);
            response.AppendData(addressBytes);
            if (carried.Add(address))
            {
                ReadOnlyMemory<byte> held = default;
                if (unit.Kept)
                {
                    held = unit.Content;
                }

                contents.Add(new(address, ScryDisclosureContentKind.External, unit.Content.Length, held));
            }

            for (var slot = 0; slot < unit.Rows.Count; slot++)
            {
                var row = unit.Rows[slot];
                if (string.IsNullOrEmpty(row.Source) ||
                    row.Key.Count == 0)
                {
                    throw new ArgumentException("A row of an external disclosure has to name its source and its key: it is found by them.", nameof(disclosure));
                }

                entities.Add(
                    new(ordinal, slot, row.Source, ScryDisclosureEntity.KeyOf(row.Key), Via: "")
                    {
                        Version = row.Version
                    });
            }
        }

        var source = disclosure.Source;
        if (source.Length == 0 &&
            entities.Count > 0)
        {
            source = entities[0].Source;
        }

        Span<byte> hash = stackalloc byte[ScryDisclosureAddress.Size];
        response.GetHashAndReset(hash);
        var at = settings.Clock.GetUtcNow();
        var record = new ScryExternalRecord(Guid.CreateVersion7(), at);
        var batch = new ScryDisclosureBatch
        {
            EventId = record.Id,
            Begin = new(record.Id, at, ScryDisclosureKind.External, source)
            {
                Caller = disclosure.Recipient,
                Agent = disclosure.Agent,
                Correlation = disclosure.Correlation,
                ContentType = disclosure.ContentType,
                Sensitive = disclosure.Sensitive,
                Node = settings.Node
            },
            Units = units,
            Entities = entities,
            Contents = contents,
            Close = new(ScryDisclosureOutcome.Released, units.Count)
            {
                At = at,
                Response = ScryDisclosureAddress.From(hash)
            }
        };
        return (record, batch);
    }

    // Addressed as content is: its kind, then its bytes, under the same key.
    static ScryDisclosureAddress Address(byte[]? key, ReadOnlySpan<byte> content)
    {
        var tagged = new byte[content.Length + 1];
        tagged[0] = (byte) ScryDisclosureContentKind.External;
        content.CopyTo(tagged.AsSpan(1));
        return DisclosureRuntime.Address(key, tagged);
    }
}
