namespace Scry;

/// <summary>
/// Reads a disclosure store: the three questions the record exists to answer, and what a screen over
/// it needs beside them. Implemented by a store; the in-memory one ships here.
/// </summary>
/// <remarks>
/// Every listing is newest first and takes up where <c>after</c> left off, so a caller reads as far
/// as it wants and asks again for more. Nothing here records that the store was read — that is the
/// caller's to do, with a <see cref="ScryDisclosureReview"/> through the sink, before it shows what
/// it read to anybody.
/// </remarks>
// begin-snippet: disclosureReader
public interface IScryDisclosureReader
{
    /// <summary>
    /// Who received a row: every release of a unit read from it, and every reviewer who has since
    /// opened an event that released one. An event that carried the row in several units is listed
    /// once for each, in the order they were sent.
    /// </summary>
    /// <param name="source">
    /// The row's source, by the wire name of the top of its hierarchy: the name
    /// <see cref="ScryDisclosureEntity.Source"/> records it under.
    /// </param>
    /// <param name="key">The row's primary-key values, in the key's own order.</param>
    /// <param name="from">The earliest time to answer for, or null for no bound.</param>
    /// <param name="to">The latest time to answer for, or null for no bound.</param>
    /// <param name="after">Where an earlier listing left off, or null to start at the newest.</param>
    /// <param name="cancel">Stops the listing.</param>
    IAsyncEnumerable<ScryRowDisclosure> ReceiversOf(
        string source,
        IReadOnlyList<object?> key,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default);

    /// <summary>
    /// What a caller received in a range of time: the events and how each ended, without their
    /// content. An event that was withdrawn before anything of it left is not among them.
    /// </summary>
    /// <param name="caller">Who received them. Null asks for the answers recorded against nobody.</param>
    /// <param name="from">The earliest time to answer for.</param>
    /// <param name="to">The latest time to answer for.</param>
    /// <param name="after">Where an earlier listing left off, or null to start at the newest.</param>
    /// <param name="cancel">Stops the listing.</param>
    IAsyncEnumerable<ScryDisclosureEntry> ReceivedBy(
        string? caller,
        DateTimeOffset from,
        DateTimeOffset to,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default);

    /// <summary>
    /// The events in which a caller was returned a member of a source, for at least one row. Empty
    /// means it never was.
    /// </summary>
    IAsyncEnumerable<ScryDisclosureEntry> MemberReceivedBy(
        string? caller,
        string source,
        string member,
        ScryDisclosureCursor? after = null,
        Cancel cancel = default);

    /// <summary>
    /// One event with everything recorded under it, content included, or null where the store holds
    /// no such event.
    /// </summary>
    ValueTask<ScryDisclosedResponse?> Reconstruct(Guid eventId, Cancel cancel = default);

    /// <summary>
    /// One piece of content by its address, or null where the store never held anything under it. What
    /// a binary value named in a row was: a row carries such a value's address and length, and the
    /// bytes are here where the store kept them.
    /// </summary>
    ValueTask<ScryDisclosureContent?> Content(ScryDisclosureAddress address, Cancel cancel = default);

    /// <summary>
    /// What a shape's address names: the members every answer of that shape read, each with what was
    /// done with it, or null where the store holds no shape under it. How a listing says which
    /// members an event returned without rebuilding the event.
    /// </summary>
    ValueTask<ScryDisclosureShape?> Shape(ScryDisclosureAddress address, Cancel cancel = default);

    /// <summary>
    /// The sources and members the store has recorded anything about: what a screen offers to ask
    /// about, without needing the schema.
    /// </summary>
    ValueTask<ScryDisclosureCatalog> Catalog(Cancel cancel = default);

    /// <summary>The questions reviewers have asked, newest first.</summary>
    IAsyncEnumerable<ScryDisclosureReview> Reviews(ScryDisclosureCursor? after = null, Cancel cancel = default);
}
// end-snippet

/// <summary>
/// Where a listing left off: the time and id of the last item read — and, for a listing of a row's
/// receivers, which unit of that event it was, since one event can carry a row in several.
/// </summary>
/// <param name="At">When the last item read was made: an event's time, or a review's.</param>
/// <param name="Id">Its id.</param>
/// <param name="Ordinal">
/// Which unit the last item read was, in a listing of a row's receivers. Left out, such a listing is
/// taken up after everything of that event.
/// </param>
public readonly record struct ScryDisclosureCursor(DateTimeOffset At, Guid Id, int Ordinal = int.MaxValue)
{
    /// <summary>Where a listing of events that ended on <paramref name="entry"/> is taken up from.</summary>
    public static ScryDisclosureCursor After(ScryDisclosureEntry entry) =>
        new(entry.Event.At, entry.Event.Id);

    /// <summary>Where a listing of a row's receivers that ended on <paramref name="row"/> is taken up from.</summary>
    public static ScryDisclosureCursor After(ScryRowDisclosure row) =>
        new(row.Review?.At ?? row.Event.At, row.Review?.Id ?? row.Event.Id, row.Ordinal);

    /// <summary>Where a listing of reviews that ended on <paramref name="review"/> is taken up from.</summary>
    public static ScryDisclosureCursor After(ScryDisclosureReview review) =>
        new(review.At, review.Id);
}

/// <summary>One event as a listing shows it: its header, and how it ended.</summary>
/// <param name="Event">The header.</param>
/// <param name="Close">
/// How it ended, or null where no close was recorded — the process died between accepting the record
/// and writing the answer, so the answer may have been sent.
/// </param>
public sealed record ScryDisclosureEntry(ScryDisclosureEvent Event, ScryDisclosureClose? Close);

/// <summary>
/// One time a row left the server: which event released a unit read from it, to whom, and the
/// address of that unit — the version of the row that was sent.
/// </summary>
/// <param name="Event">The event that released it.</param>
/// <param name="Ordinal">Which unit of the event it was.</param>
/// <param name="Content">The unit's address. Two releases with the same one sent the same content.</param>
/// <param name="Via">
/// How the unit reached the row, as <see cref="ScryDisclosureEntity.Via"/> says. Where one unit
/// reached it more than one way — a row joined to itself — the first of them.
/// </param>
public sealed record ScryRowDisclosure(ScryDisclosureEvent Event, int Ordinal, ScryDisclosureAddress Content, string Via)
{
    /// <summary>
    /// How <see cref="Event"/> ended, or null where no close was recorded — the process stopped
    /// before it could write one, so the unit may have been sent.
    /// </summary>
    public ScryDisclosureClose? Close { get; init; }

    /// <summary>
    /// Set where this entry is somebody reading the store rather than the release itself: the review
    /// that opened or exported <see cref="Event"/>. Its reviewer saw what the event's caller did.
    /// </summary>
    public ScryDisclosureReview? Review { get; init; }
}

/// <summary>One event as the store holds it: its header, how it ended, and its units with their content.</summary>
/// <param name="Event">The header.</param>
/// <param name="Close">How it ended, or null where no close record was written.</param>
/// <param name="Request">The request that was answered, as canonical JSON, where there was one and the store still holds it.</param>
/// <param name="Shape">The members the answer read, where it has a shape.</param>
/// <param name="Units">Every unit recorded, in order, released or not: <see cref="Close"/> says how many were.</param>
public sealed record ScryDisclosedResponse(
    ScryDisclosureEvent Event,
    ScryDisclosureClose? Close,
    ReadOnlyMemory<byte>? Request,
    ScryDisclosureShape? Shape,
    IReadOnlyList<ScryDisclosedUnit> Units);

/// <summary>One unit of an event, with what the store holds of it.</summary>
/// <param name="Ordinal">Where it sat in the answer.</param>
/// <param name="Content">Its address, kind and length — and its bytes, unless they were recorded by digest alone or have since been erased.</param>
/// <param name="Entities">The rows it was read from.</param>
/// <param name="Erased">Whether the bytes were removed by an erasure.</param>
public sealed record ScryDisclosedUnit(
    int Ordinal,
    ScryDisclosureContent Content,
    IReadOnlyList<ScryDisclosureEntity> Entities,
    bool Erased);

/// <summary>What a store has recorded anything about.</summary>
/// <param name="Sources">Each source with the members some answer read, in name order.</param>
public sealed record ScryDisclosureCatalog(IReadOnlyList<ScryDisclosureCatalogSource> Sources);

/// <summary>One source a store has recorded answers from.</summary>
/// <param name="Name">Its wire name.</param>
/// <param name="Keyed">Whether any answer recorded a row of it by key.</param>
/// <param name="Members">The members answers read, in name order.</param>
public sealed record ScryDisclosureCatalogSource(string Name, bool Keyed, IReadOnlyList<ScryDisclosureCatalogMember> Members);

/// <summary>One member a store has recorded a read of.</summary>
public sealed record ScryDisclosureCatalogMember(string Name, bool Sensitive);

/// <summary>
/// Removes a row's content from a store, where the store can. Events, addresses and keys stay: the
/// record that something was sent outlives what it was.
/// </summary>
public interface IScryDisclosureEraser
{
    /// <summary>
    /// Removes every piece of content in which the row appears, and records that it was done.
    /// Erasure wins over reconstruction: content another row shared goes with it.
    /// </summary>
    /// <param name="source">The row's source.</param>
    /// <param name="key">The row's primary-key values, in the key's own order.</param>
    /// <param name="by">Who asked.</param>
    /// <param name="cancel">Stops waiting for the store.</param>
    ValueTask<ScryDisclosureErasure> EraseAsync(string source, IReadOnlyList<object?> key, string? by, Cancel cancel = default);

    /// <summary>The erasures made, newest first.</summary>
    IAsyncEnumerable<ScryDisclosureErasure> Erasures(Cancel cancel = default);
}

/// <summary>How a store stands: what it has yet to do, and how far it has got.</summary>
public interface IScryDisclosureStatus
{
    ValueTask<ScryDisclosureStoreStatus> Status(Cancel cancel = default);
}

/// <summary>A store's own account of itself.</summary>
/// <param name="Pending">Batches accepted and not yet written where a reader finds them.</param>
/// <param name="PendingBytes">How many bytes those are, where the store counts them.</param>
/// <param name="OldestPending">When the oldest of them was accepted, or null where none is waiting.</param>
/// <param name="Events">How many events a reader can find.</param>
public sealed record ScryDisclosureStoreStatus(long Pending, long PendingBytes, DateTimeOffset? OldestPending, long Events)
{
    /// <summary>The last record of the hash chain, where the store keeps one.</summary>
    public ScryDisclosureAddress? ChainHead { get; init; }

    /// <summary>When the chain was last verified, and whether it held. Null where it never was.</summary>
    public ScryDisclosureChainCheck? LastVerification { get; init; }

    /// <summary>
    /// What is in the way of the record being whole, where anything is: a store that keeps failing
    /// to move on what it accepted, a batch that will not read back. Null where nothing is.
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>
/// Checks a store's own evidence that its record is still what was written, where it keeps any: for
/// the SQL Server store, its hash chain.
/// </summary>
public interface IScryDisclosureVerifier
{
    /// <summary>Checks a stretch of that evidence.</summary>
    /// <param name="from">Where to start, or null for the beginning.</param>
    /// <param name="count">How many records to check.</param>
    /// <param name="cancel">Stops the check.</param>
    ValueTask<ScryDisclosureChainCheck> Verify(long? from = null, int count = 10_000, Cancel cancel = default);
}

/// <summary>The result of verifying a stretch of the hash chain.</summary>
/// <param name="At">When it was verified.</param>
/// <param name="Records">How many records were checked.</param>
/// <param name="Intact">Whether every one followed from the one before it.</param>
public sealed record ScryDisclosureChainCheck(DateTimeOffset At, long Records, bool Intact)
{
    /// <summary>Where the chain broke, for one that did.</summary>
    public long? BrokenAt { get; init; }
}
