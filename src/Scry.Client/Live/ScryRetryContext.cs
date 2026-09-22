namespace Scry;

/// <summary>What an <see cref="IScryRetryPolicy"/> is told about the attempt it is being asked to time.</summary>
/// <param name="PreviousRetryCount">
/// How many attempts in a row have ended without an answer arriving. Zero for the first attempt after
/// a connection that did deliver — so a stream the server ended on schedule is asked for again at
/// once, and only a run of failures is backed away from.
/// </param>
/// <param name="ElapsedTime">How long it has been since an answer last arrived, or since the live query began.</param>
/// <param name="RetryReason">What ended the last attempt, or null where the server ended it without a failure.</param>
public sealed record ScryRetryContext(int PreviousRetryCount, TimeSpan ElapsedTime, Exception? RetryReason);
