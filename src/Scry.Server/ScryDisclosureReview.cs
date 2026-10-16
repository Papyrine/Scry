namespace Scry;

/// <summary>
/// One question somebody asked of the disclosure store. Reading the record of who saw what is itself
/// seeing it, so the reading is recorded too — through the same sink, and before it is answered.
/// </summary>
/// <param name="Id">Names the review.</param>
/// <param name="At">When it was asked, in UTC.</param>
/// <param name="Question">What was asked.</param>
/// <remarks>
/// A question that only lists events shows who and when, and records the question. One that shows
/// content — an event opened, a result exported — names the events in <see cref="Events"/>, and from
/// then on the reviewer is among those who received their rows.
/// </remarks>
public sealed record ScryDisclosureReview(Guid Id, DateTimeOffset At, ScryDisclosureQuestion Question)
{
    /// <summary>Who asked. Null only where anonymous callers are allowed.</summary>
    public string? Reviewer { get; init; }

    /// <summary>What the question was asked about, as canonical JSON kept by address.</summary>
    public ScryDisclosureAddress? Parameters { get; init; }

    /// <summary>How many results the answer held.</summary>
    public int Results { get; init; }

    /// <summary>The events whose content the answer showed. Empty for an answer that showed none.</summary>
    public IReadOnlyList<Guid> Events { get; init; } = [];

    /// <summary>The node that answered.</summary>
    public string? Node { get; init; }
}

/// <summary>What a reviewer asked of the store.</summary>
public enum ScryDisclosureQuestion : byte
{
    /// <summary>Who received a row.</summary>
    Row = 1,

    /// <summary>What a caller received.</summary>
    Caller = 2,

    /// <summary>Whether a caller received a member.</summary>
    Member = 3,

    /// <summary>One event, with its content.</summary>
    Event = 4,

    /// <summary>A result written out as a file.</summary>
    Export = 5,

    /// <summary>The state of the store.</summary>
    Status = 6,

    /// <summary>A verification of the hash chain.</summary>
    Verify = 7,

    /// <summary>The erasure of a row's content.</summary>
    Erase = 8
}

/// <summary>
/// That a row's content was removed from the store, and at whose asking. The record of the erasure
/// outlives what it erased.
/// </summary>
/// <param name="At">When, in UTC.</param>
/// <param name="By">Who asked.</param>
/// <param name="Source">The source the row belongs to.</param>
/// <param name="Key">The row's key, as an entity ref records it.</param>
/// <param name="Units">How many pieces of content were removed.</param>
public sealed record ScryDisclosureErasure(DateTimeOffset At, string? By, string Source, string Key, int Units);
