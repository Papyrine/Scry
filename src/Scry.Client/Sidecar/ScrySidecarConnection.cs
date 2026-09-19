namespace Scry;

/// <summary>
/// One connection a live query made: the HTTP exchange that carried it, and the events that came
/// back over it. A live query that was cut and asked again has one of these per attempt.
/// </summary>
public sealed class ScrySidecarConnection
{
    Lock sync = new();
    List<ScrySidecarEvent> events = [];
    int maxEvents;
    int dropped;

    internal ScrySidecarConnection(int attempt, string? resumedFrom, int maxEvents)
    {
        Attempt = attempt;
        ResumedFrom = resumedFrom;
        this.maxEvents = maxEvents;
        Started = DateTimeOffset.Now;
    }

    /// <summary>Which attempt this was, counting from one.</summary>
    public int Attempt { get; }

    public DateTimeOffset Started { get; }

    /// <summary>
    /// The answer this connection told the server it already held, as
    /// <see cref="ScryLive.LastEventIdHeader"/>. Null on a first connection, which holds none.
    /// </summary>
    public string? ResumedFrom { get; }

    public int? Status { get; internal set; }

    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; internal set; } = [];

    /// <summary>How long the connection was held. Null while it is still open.</summary>
    public TimeSpan? Duration { get; internal set; }

    /// <summary>
    /// How the connection finished: the server's own reason from its <see cref="ScryLive.End"/>
    /// event, <c>error</c>, or <c>cut</c> where it stopped without saying anything. Null while it is
    /// still open.
    /// </summary>
    public string? Ended { get; internal set; }

    /// <summary>Whether the connection is still being read.</summary>
    public bool Open => Ended is null;

    /// <summary>
    /// Events dropped to stay within <see cref="ScrySidecarOptions.MaxSubscriptionEvents"/>. A live
    /// query held open for hours is mostly heartbeats, and the recent ones are the interesting ones.
    /// </summary>
    public int Dropped
    {
        get
        {
            lock (sync)
            {
                return dropped;
            }
        }
    }

    /// <summary>A snapshot of the events read over this connection, oldest first.</summary>
    public IReadOnlyList<ScrySidecarEvent> Events
    {
        get
        {
            lock (sync)
            {
                return [.. events];
            }
        }
    }

    internal void Add(ScrySidecarEvent captured)
    {
        lock (sync)
        {
            events.Add(captured);
            while (events.Count > maxEvents)
            {
                events.RemoveAt(0);
                dropped++;
            }
        }
    }

    /// <summary>
    /// Forgets the data kept for every answer but the most recent ones, so that a connection held
    /// open indefinitely does not accumulate payloads indefinitely with it.
    /// </summary>
    internal void Trim(int keep)
    {
        lock (sync)
        {
            var seen = 0;
            for (var i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Json is null)
                {
                    continue;
                }

                seen++;
                if (seen > keep)
                {
                    events[i] = events[i] with {Json = null};
                }
            }
        }
    }
}
