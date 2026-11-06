namespace Scry;

/// <summary>
/// Something host code is about to send outside Scry: who it goes to, what sent it, and what it
/// carried, a unit at a time, each with the rows it was made from. What
/// <see cref="ScryExternalDisclosures.RecordAsync"/> records.
/// </summary>
/// <remarks>
/// Scry cannot see what a handler, a job or an export sends, so it cannot vouch that this says all of
/// it. What it can do is keep it in the same record as every answer it sent itself, so that "who
/// received this row" has one place to be asked.
/// </remarks>
public sealed class ScryExternalDisclosure
{
    /// <summary>
    /// Who it is sent to: a person, an address, a system. Recorded as the event's caller, which is
    /// what "who received this row" answers with.
    /// </summary>
    public required string Recipient { get; init; }

    /// <summary>
    /// What sent it: the handler, the job, the export. Recorded as the event's agent. Null records
    /// nothing.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// The source it is chiefly about, by wire name. Empty, the default, takes the source of the first
    /// row of the first unit, or none where no unit names a row.
    /// </summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// What ties it to other events: the id of the command whose handler sent it, the run of a job.
    /// </summary>
    public string? Correlation { get; init; }

    /// <summary>The media type it was sent as, where it has one.</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether it carried something the host counts as sensitive.</summary>
    public bool Sensitive { get; init; }

    /// <summary>What it carried, in the order it was sent.</summary>
    public required IReadOnlyList<ScryExternalUnit> Units { get; init; }
}

/// <summary>One thing an external disclosure carried, and the rows it was made from.</summary>
/// <remarks>
/// A unit is what an erasure finds by row: one that names a row is erased with it. So a message about
/// several people is best recorded as a unit per person where it can be split, or as one unit naming
/// every row it holds anything of where it cannot.
/// </remarks>
public sealed class ScryExternalUnit
{
    /// <summary>What was sent, as it was sent. Addressed as content is, and kept once however often it is sent.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>
    /// Whether the bytes are kept, rather than their digest and length alone. True by default: what
    /// was sent is what a review of the record will want to read. False for something too large to
    /// keep, or something whose bytes the record should not hold.
    /// </summary>
    public bool Kept { get; init; } = true;

    /// <summary>The rows it holds anything of. Empty for a unit made from no row.</summary>
    public IReadOnlyList<ScryDisclosureRow> Rows { get; init; } = [];
}

/// <summary>An external disclosure as it was recorded.</summary>
/// <param name="Id">The event's id, which the record and its explorer name it by.</param>
/// <param name="At">When it was recorded, in UTC.</param>
public readonly record struct ScryExternalRecord(Guid Id, DateTimeOffset At);
