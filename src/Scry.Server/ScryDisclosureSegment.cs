namespace Scry;

/// <summary>
/// One segment of a disclosure record kept in segments, as the SQL Server store keeps it: every
/// table of the record for a stretch of it, written to until the next is started and dropped
/// whole when it is retired.
/// </summary>
/// <param name="Number">Which segment, counted from one.</param>
/// <param name="Schema">Where its tables are, while it is kept: the SQL Server store's schema.</param>
/// <param name="Opened">When it was started.</param>
public sealed record ScryDisclosureSegment(int Number, string Schema, DateTimeOffset Opened)
{
    /// <summary>When the next segment was started, or null for the one being written to.</summary>
    public DateTimeOffset? Sealed { get; init; }

    /// <summary>The last link the chain had when the next segment was started.</summary>
    public long? Link { get; init; }

    /// <summary>That link's hash: where the chain stood when this segment was left.</summary>
    public ScryDisclosureAddress? Head { get; init; }

    /// <summary>
    /// Whether its tables are ledger tables: the record is kept whole in them, and it is never retired.
    /// </summary>
    public bool Ledgered { get; init; }

    /// <summary>Whether it is being retired: still read, and no longer written to.</summary>
    public bool Retiring { get; init; }

    /// <summary>When it was dropped, or null for one that is kept.</summary>
    public DateTimeOffset? Retired { get; init; }

    /// <summary>Whether it was handed to the store's <c>Retire</c> before it was dropped.</summary>
    public bool HandedOn { get; init; }

    /// <summary>How many batches it held when it was retired.</summary>
    public long? Batches { get; init; }

    /// <summary>How many answers it held when it was retired.</summary>
    public long? Events { get; init; }

    /// <summary>How much of the database it took when it was retired, in bytes.</summary>
    public long? Bytes { get; init; }

    /// <summary>When the first answer in it was given.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>When the last answer in it was given.</summary>
    public DateTimeOffset? To { get; init; }
}

/// <summary>
/// When the disclosure record starts a new segment, and how many it keeps, as the record holds them.
/// </summary>
/// <param name="RollAfter">How long a segment is written to, or null for no limit.</param>
/// <param name="RollAtBytes">How large a segment grows, or null for no limit.</param>
/// <param name="Keep">How many segments are kept, or null for all of them.</param>
/// <param name="HandsOn">Whether a segment is handed on before it is dropped. No node without something to hand it to retires one.</param>
public sealed record ScryDisclosureRetention(TimeSpan? RollAfter, long? RollAtBytes, int? Keep, bool HandsOn)
{
    /// <summary>How long a segment is kept once it is rolled away from, or null for no limit.</summary>
    public TimeSpan? KeepFor { get; init; }
}
