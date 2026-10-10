namespace Scry;

/// <summary>
/// One answer handed to one caller: who, when, what kind of answer it was, and where the content it
/// carried is kept. The header of a disclosure — the rows it carried follow as
/// <see cref="ScryDisclosureUnit"/>s, and a <see cref="ScryDisclosureClose"/> says how it ended.
/// </summary>
/// <param name="Id">
/// Names the event to everything recorded under it. Made in time order, so events sort by when they
/// began.
/// </param>
/// <param name="At">When the answer began to be made, in UTC.</param>
/// <param name="Kind">What was answered.</param>
/// <param name="Source">
/// The root source the answer was read from, by its wire name. Empty for an answer about no source:
/// the schema, a caller's capabilities.
/// </param>
/// <remarks>
/// An event holds addresses and never content. What was sent is stored once, by address, however many
/// events sent it.
/// </remarks>
public sealed record ScryDisclosureEvent(Guid Id, DateTimeOffset At, ScryDisclosureKind Kind, string Source)
{
    /// <summary>
    /// Who received it: what <see cref="ScryOptions.Caller"/> or
    /// <see cref="ScryDisclosureOptions.Caller"/> answered. Null only where
    /// <see cref="ScryDisclosureOptions.AllowAnonymous"/> let a caller with no name through.
    /// </summary>
    public string? Caller { get; init; }

    /// <summary>Whether this was one answer of a live query rather than a query asked once.</summary>
    public bool Subscribed { get; init; }

    /// <summary>Whether the content was sent, or only confirmed as what the caller already held.</summary>
    public ScryDisclosureDelivery Delivery { get; init; }

    /// <summary>The request that was answered, as canonical JSON. Null where no request was: a schema read.</summary>
    public ScryDisclosureAddress? Request { get; init; }

    /// <summary>
    /// The shape of what was answered: which members of which sources each row was read from. Null
    /// for an answer that has none.
    /// </summary>
    public ScryDisclosureAddress? Shape { get; init; }

    /// <summary>Whether the answer returned a member the model marks <c>[Sensitive]</c>.</summary>
    public bool Sensitive { get; init; }

    /// <summary>The schema stamp of the server that answered.</summary>
    public string? Stamp { get; init; }

    /// <summary>
    /// What ties this event to others made for the same request: the entries of one batch, the
    /// answers of one live query.
    /// </summary>
    public string? Correlation { get; init; }

    /// <summary>The node that answered.</summary>
    public string? Node { get; init; }

    /// <summary>
    /// The media type the answer was sent as, where it was an attachment's bytes: what the model
    /// declared them to be, or what the attachment policy said this row's are. Null for everything else.
    /// </summary>
    public string? ContentType { get; init; }
}

/// <summary>What an answer was.</summary>
public enum ScryDisclosureKind
{
    /// <summary>The rows of a query.</summary>
    List,

    /// <summary>One page of them.</summary>
    Page,

    /// <summary>One row, or none.</summary>
    Single,

    /// <summary>A value the rows were folded to: a count, a sum, whether there are any.</summary>
    Scalar,

    /// <summary>Rows sent as they were read.</summary>
    Stream,

    /// <summary>The bytes of one attachment.</summary>
    Attachment,

    /// <summary>How a command stood: pending, completed, or failed.</summary>
    CommandReceipt,

    /// <summary>The commands a caller may send.</summary>
    Capabilities,

    /// <summary>The allow-listed schema.</summary>
    Schema,

    /// <summary>The SQL a query would run.</summary>
    SqlPreview,

    /// <summary>
    /// A query refused because a row policy denied a row and says so rather than hiding it. No content
    /// left, but the caller learned that rows it may not see matched.
    /// </summary>
    Denial
}

/// <summary>Whether an answer's content crossed to the caller this time.</summary>
public enum ScryDisclosureDelivery
{
    /// <summary>The content was handed to the transport.</summary>
    Sent,

    /// <summary>
    /// The caller named the content it already held and was told it still stands, so nothing was sent
    /// again. Recorded because the caller demonstrably holds it.
    /// </summary>
    Confirmed
}

/// <summary>
/// How an event ended: what became of the answer, and how many of its units were released to the
/// transport.
/// </summary>
/// <param name="Outcome">What became of the answer.</param>
/// <param name="Units">
/// How many units were released, counted from the first. A unit recorded past this number was
/// accepted by the store and never handed over, and is not a disclosure.
/// </param>
/// <remarks>
/// <para>
/// An event with no close record belongs to a process that stopped before it could write one. Its
/// units are read as "may have been sent", which is the direction that never under-reports.
/// </para>
/// <para>
/// An event can carry two. An answer is closed as released once the store holds all of it, which is
/// before the last step that can still fail the request; where that step does fail, a second close
/// says what really left. The one written last is the one that stands.
/// </para>
/// </remarks>
public sealed record ScryDisclosureClose(ScryDisclosureOutcome Outcome, int Units)
{
    /// <summary>When the answer ended, in UTC.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// An address over the addresses of the released units, in order: two answers carrying the same
    /// content in the same order have the same one.
    /// </summary>
    public ScryDisclosureAddress? Response { get; init; }
}

/// <summary>What became of an answer.</summary>
public enum ScryDisclosureOutcome
{
    /// <summary>All of it was handed to the transport.</summary>
    Released,

    /// <summary>
    /// It was cut short — by a limit, or by a later step failing the request — after the units counted
    /// were handed over.
    /// </summary>
    Truncated,

    /// <summary>The caller stopped reading, after the units counted were handed over.</summary>
    Canceled,

    /// <summary>It failed part-way, after the units counted were handed over.</summary>
    Failed,

    /// <summary>
    /// It was accepted by the store and then not sent at all: a later step failed before anything was
    /// released.
    /// </summary>
    Retracted
}
