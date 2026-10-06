namespace Scry;

/// <summary>
/// A disclosure store held in memory: the sink answers are recorded through, and the reader, eraser
/// and status over what it was handed. For tests, for a host whose record may live and die with the
/// process, and as the plainest statement of what a store keeps.
/// </summary>
/// <remarks>
/// Accepting a batch is a copy under a lock, so what "accepted" is worth here is this process's
/// memory: all of it is gone when the process stops. A host that has to keep its record uses a store
/// that writes somewhere.
/// </remarks>
public sealed class ScryMemoryDisclosureStore(TimeProvider? clock = null) :
    IScryDisclosureSink,
    IScryDisclosureReader,
    IScryDisclosureEraser,
    IScryDisclosureStatus
{
    sealed class Recorded
    {
        public ScryDisclosureEvent? Header;
        public ScryDisclosureClose? Close;
        public int CloseSequence = -1;
        public HashSet<int> Sequences = [];
        public List<ScryDisclosureUnit> Units = [];
        public List<ScryDisclosureEntity> Entities = [];
    }

    sealed class Held(ScryDisclosureContentKind kind, int length)
    {
        public ScryDisclosureContentKind Kind => kind;
        public int Length => length;
        public byte[]? Bytes;
        public bool Erased;
    }

    Lock gate = new();
    TimeProvider clock = clock ?? TimeProvider.System;
    Dictionary<Guid, Recorded> events = [];
    Dictionary<ScryDisclosureAddress, Held> contents = [];
    Dictionary<ScryDisclosureAddress, ScryDisclosureShape> shapes = [];
    Dictionary<(string Source, string Key), List<(Guid Event, ScryDisclosureEntity Entity)>> rows = [];
    Dictionary<Guid, ScryDisclosureReview> reviews = [];
    List<ScryDisclosureErasure> erasures = [];

    /// <summary>
    /// How many pieces of content are kept: one for each distinct thing sent, however many answers
    /// sent it.
    /// </summary>
    public int ContentCount
    {
        get
        {
            lock (gate)
            {
                return contents.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Append(ScryDisclosureBatch batch)
    {
        lock (gate)
        {
            // A reviewer's question travels alone, under its own id, and is kept once.
            if (batch.Review is { } review)
            {
                if (!reviews.TryAdd(review.Id, review))
                {
                    return;
                }

                Keep(batch);
                return;
            }

            if (!events.TryGetValue(batch.EventId, out var recorded))
            {
                events[batch.EventId] = recorded = new();
            }

            // A batch handed over twice — a journal shipping again after a crash — changes nothing the
            // second time. Checked before its content is kept, so one delivered again after an erasure
            // does not bring back what was erased.
            if (!recorded.Sequences.Add(batch.Sequence))
            {
                return;
            }

            Keep(batch);
            if (batch.Begin is { } begin)
            {
                recorded.Header = begin;
            }

            recorded.Units.AddRange(batch.Units);
            foreach (var entity in batch.Entities)
            {
                recorded.Entities.Add(entity);
                var row = (entity.Source, entity.Key);
                if (!rows.TryGetValue(row, out var disclosures))
                {
                    rows[row] = disclosures = [];
                }

                disclosures.Add((batch.EventId, entity));
            }

            // The close written last is the one that stands, whatever order they arrive in.
            if (batch.Close is { } close &&
                batch.Sequence > recorded.CloseSequence)
            {
                recorded.Close = close;
                recorded.CloseSequence = batch.Sequence;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Append(batch);
        return ValueTask.CompletedTask;
    }

    // Under the gate. Content and shapes are kept by address, so the same one arriving again — from
    // another event, as it does every time a row is sent again — is already here.
    void Keep(ScryDisclosureBatch batch)
    {
        if (batch.Shape is { } shape)
        {
            shapes.TryAdd(shape.Address, shape);
        }

        foreach (var content in batch.Contents)
        {
            if (!contents.TryGetValue(content.Address, out var held))
            {
                contents[content.Address] = held = new(content.Kind, content.Length);
            }

            if (held.Bytes is not null ||
                !content.Held)
            {
                continue;
            }

            // Copied: a batch's bytes are its sender's buffer, and are its sender's again once this
            // returns.
            held.Bytes = content.Bytes.ToArray();
            held.Erased = false;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ScryRowDisclosure> ReceiversOf(
        string source,
        IReadOnlyList<object?> key,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default)
    {
        var found = new List<ScryRowDisclosure>();
        lock (gate)
        {
            if (rows.TryGetValue((source, ScryDisclosureEntity.KeyOf(key)), out var disclosures))
            {
                var opened = new HashSet<Guid>();

                // One entry for each unit that carried the row, however many of the unit's rows it
                // was: a row joined to itself is still sent once.
                foreach (var carried in disclosures.GroupBy(_ => (_.Event, _.Entity.Ordinal)))
                {
                    var (id, entity) = carried.MinBy(_ => _.Entity.Slot);
                    var recorded = events[id];

                    // A unit past what the close counts was accepted and never handed on.
                    if (recorded.Header is not { } header ||
                        entity.Ordinal >= Released(recorded))
                    {
                        continue;
                    }

                    found.Add(
                        new(header, entity.Ordinal, recorded.Units.First(_ => _.Ordinal == entity.Ordinal).Content, entity.Via)
                        {
                            Close = recorded.Close
                        });
                    opened.Add(id);
                }

                // Whoever has since been shown one of those events was shown the row too.
                foreach (var review in reviews.Values)
                {
                    foreach (var id in review.Events.Distinct())
                    {
                        if (!opened.Contains(id))
                        {
                            continue;
                        }

                        foreach (var shown in found.Where(_ => _.Review is null && _.Event.Id == id).ToList())
                        {
                            found.Add(
                                shown with
                                {
                                    Review = review
                                });
                        }
                    }
                }
            }
        }

        return found
            .Where(_ => Within(When(_), from, to))
            .OrderByDescending(When)
            .ThenByDescending(Mark)
            .ThenBy(_ => _.Ordinal)
            .Where(_ => Before(_, after))
            .ToAsyncEnumerable();
    }

    // When a row reached somebody: when the answer was made, or when a reviewer was shown it.
    static DateTimeOffset When(ScryRowDisclosure disclosure) =>
        disclosure.Review?.At ?? disclosure.Event.At;

    // Whose reaching it this entry is: the review's where a reviewer was shown the event, else the
    // event's own.
    static Guid Mark(ScryRowDisclosure disclosure) =>
        disclosure.Review?.Id ?? disclosure.Event.Id;

    // The units of one event follow each other in the order they were sent, so within the event a
    // cursor names, what follows it is every unit after the one it names.
    static bool Before(ScryRowDisclosure disclosure, ScryDisclosureCursor? after)
    {
        if (after is { } cursor &&
            When(disclosure) == cursor.At &&
            Mark(disclosure) == cursor.Id)
        {
            return disclosure.Ordinal > cursor.Ordinal;
        }

        return Before(When(disclosure), Mark(disclosure), after);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ScryDisclosureEntry> ReceivedBy(
        string? caller,
        DateTimeOffset from,
        DateTimeOffset to,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default) =>
        Listed(_ => _.Header!.Caller == caller && Within(_.Header.At, from, to), after);

    /// <inheritdoc />
    public IAsyncEnumerable<ScryDisclosureEntry> MemberReceivedBy(
        string? caller,
        string source,
        string member,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default) =>
        Listed(_ => _.Header!.Caller == caller && Returned(_, source, member), after);

    // Whether an event returned a member of a source for at least one row: its shape says the member
    // was returned, and something of that source left — a row named by key, or for a source with no
    // key to name one by, any unit at all.
    bool Returned(Recorded recorded, string source, string member)
    {
        if (recorded.Header!.Shape is not { } address ||
            !shapes.TryGetValue(address, out var shape) ||
            !shape.Fields.Any(_ => _.Source == source && _.Member == member && _.Use == ScryDisclosureFieldUse.Returned))
        {
            return false;
        }

        var released = Released(recorded);
        if (recorded.Entities.Any(_ => _.Source == source))
        {
            return recorded.Entities.Any(_ => _.Source == source && _.Ordinal < released);
        }

        return recorded.Units.Any(_ => _.Ordinal < released);
    }

    IAsyncEnumerable<ScryDisclosureEntry> Listed(Func<Recorded, bool> wanted, ScryDisclosureCursor? after)
    {
        List<ScryDisclosureEntry> found;
        lock (gate)
        {
            found =
            [
                .. events.Values
                    .Where(_ => _.Header is not null && !Withdrawn(_) && wanted(_))
                    .Select(_ => new ScryDisclosureEntry(_.Header!, _.Close))
            ];
        }

        return found
            .OrderByDescending(_ => _.Event.At)
            .ThenByDescending(_ => _.Event.Id)
            .Where(_ => Before(_.Event.At, _.Event.Id, after))
            .ToAsyncEnumerable();
    }

    /// <inheritdoc />
    public ValueTask<ScryDisclosedResponse?> Reconstruct(Guid eventId, Cancel cancel = default)
    {
        lock (gate)
        {
            if (!events.TryGetValue(eventId, out var recorded) ||
                recorded.Header is not { } header)
            {
                return new((ScryDisclosedResponse?) null);
            }

            ReadOnlyMemory<byte>? request = null;
            if (header.Request is { } asked &&
                contents.TryGetValue(asked, out var body) &&
                body.Bytes is { } bytes)
            {
                request = bytes;
            }

            ScryDisclosureShape? shape = null;
            if (header.Shape is { } described)
            {
                shapes.TryGetValue(described, out shape);
            }

            var units = recorded.Units
                .OrderBy(_ => _.Ordinal)
                .Select(_ => Unit(_, recorded))
                .ToList();
            return new(new ScryDisclosedResponse(header, recorded.Close, request, shape, units));
        }
    }

    ScryDisclosedUnit Unit(ScryDisclosureUnit unit, Recorded recorded)
    {
        var entities = recorded.Entities
            .Where(_ => _.Ordinal == unit.Ordinal)
            .OrderBy(_ => _.Slot)
            .ToList();
        if (!contents.TryGetValue(unit.Content, out var held))
        {
            return new(unit.Ordinal, new(unit.Content, ScryDisclosureContentKind.Row, 0, default), entities, Erased: false);
        }

        return new(unit.Ordinal, Content(unit.Content, held), entities, held.Erased);
    }

    /// <inheritdoc />
    public ValueTask<ScryDisclosureContent?> Content(ScryDisclosureAddress address, Cancel cancel = default)
    {
        lock (gate)
        {
            if (!contents.TryGetValue(address, out var held))
            {
                return new((ScryDisclosureContent?) null);
            }

            return new(Content(address, held));
        }
    }

    static ScryDisclosureContent Content(ScryDisclosureAddress address, Held held)
    {
        ReadOnlyMemory<byte> bytes = default;
        if (held.Bytes is { } kept)
        {
            bytes = kept;
        }

        return new(address, held.Kind, held.Length, bytes);
    }

    /// <inheritdoc />
    public ValueTask<ScryDisclosureShape?> Shape(ScryDisclosureAddress address, Cancel cancel = default)
    {
        lock (gate)
        {
            return new(shapes.GetValueOrDefault(address));
        }
    }

    /// <inheritdoc />
    public ValueTask<ScryDisclosureCatalog> Catalog(Cancel cancel = default)
    {
        lock (gate)
        {
            var members = new SortedDictionary<string, SortedDictionary<string, bool>>(StringComparer.Ordinal);
            SortedDictionary<string, bool> Source(string name)
            {
                if (!members.TryGetValue(name, out var found))
                {
                    members[name] = found = new(StringComparer.Ordinal);
                }

                return found;
            }

            foreach (var recorded in events.Values)
            {
                if (recorded.Header is {Source.Length: > 0} header)
                {
                    Source(header.Source);
                }
            }

            foreach (var field in shapes.Values.SelectMany(_ => _.Fields))
            {
                var source = Source(field.Source);
                source[field.Member] = field.Sensitive || source.GetValueOrDefault(field.Member);
            }

            var keyed = rows.Keys.Select(_ => _.Source).ToHashSet(StringComparer.Ordinal);
            foreach (var name in keyed)
            {
                Source(name);
            }

            return new(
                new ScryDisclosureCatalog(
                [
                    .. members.Select(source => new ScryDisclosureCatalogSource(
                        source.Key,
                        keyed.Contains(source.Key),
                        [.. source.Value.Select(_ => new ScryDisclosureCatalogMember(_.Key, _.Value))]))
                ]));
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ScryDisclosureReview> Reviews(ScryDisclosureCursor? after = null, Cancel cancel = default)
    {
        List<ScryDisclosureReview> found;
        lock (gate)
        {
            found = [.. reviews.Values];
        }

        return found
            .OrderByDescending(_ => _.At)
            .ThenByDescending(_ => _.Id)
            .Where(_ => Before(_.At, _.Id, after))
            .ToAsyncEnumerable();
    }

    /// <inheritdoc />
    public ValueTask<ScryDisclosureErasure> EraseAsync(string source, IReadOnlyList<object?> key, string? by, Cancel cancel = default)
    {
        var canonical = ScryDisclosureEntity.KeyOf(key);
        lock (gate)
        {
            var removed = 0;
            if (rows.TryGetValue((source, canonical), out var disclosures))
            {
                foreach (var (id, entity) in disclosures)
                {
                    var unit = events[id].Units.First(_ => _.Ordinal == entity.Ordinal);
                    if (!contents.TryGetValue(unit.Content, out var held) ||
                        held.Erased)
                    {
                        continue;
                    }

                    // Erasure wins over reconstruction: another row that happened to be sent as the
                    // same bytes loses them too.
                    held.Bytes = null;
                    held.Erased = true;
                    removed++;
                }
            }

            var erasure = new ScryDisclosureErasure(clock.GetUtcNow(), by, source, canonical, removed);
            erasures.Add(erasure);
            return new(erasure);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ScryDisclosureErasure> Erasures(Cancel cancel = default)
    {
        List<ScryDisclosureErasure> found;
        lock (gate)
        {
            found = [.. erasures];
        }

        return found
            .OrderByDescending(_ => _.At)
            .ToAsyncEnumerable();
    }

    /// <inheritdoc />
    public ValueTask<ScryDisclosureStoreStatus> Status(Cancel cancel = default)
    {
        lock (gate)
        {
            // Nothing is ever waiting: a batch is where a reader finds it by the time it is accepted.
            return new(new ScryDisclosureStoreStatus(Pending: 0, PendingBytes: 0, OldestPending: null, events.Values.Count(_ => _.Header is not null)));
        }
    }

    // How many of an event's units left. All of them where no close says: the process stopped before
    // it could, and what it accepted may have gone.
    static int Released(Recorded recorded) =>
        recorded.Close?.Units ?? int.MaxValue;

    static bool Withdrawn(Recorded recorded) =>
        recorded.Close is {Outcome: ScryDisclosureOutcome.Retracted};

    static bool Within(DateTimeOffset at, DateTimeOffset? from, DateTimeOffset? to) =>
        (from is null || at >= from) &&
        (to is null || at <= to);

    // Newest first, so what follows a cursor is everything older than it — and, at the same instant,
    // everything whose id sorts before its.
    static bool Before(DateTimeOffset at, Guid id, ScryDisclosureCursor? after)
    {
        if (after is not { } cursor)
        {
            return true;
        }

        if (at != cursor.At)
        {
            return at < cursor.At;
        }

        return id.CompareTo(cursor.Id) < 0;
    }
}
