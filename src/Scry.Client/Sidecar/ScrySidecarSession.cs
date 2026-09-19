namespace Scry;

/// <summary>
/// One live query, for as long as the app holds it open — across every connection it took to keep it
/// answered. The one thing in the sidecar's log that goes on changing after it was recorded.
/// </summary>
/// <remarks>
/// <para>
/// Written from two places that see different halves of the same live query. The connections and
/// their events come off the wire, through <see cref="ScrySidecarHandler"/>; the states and the
/// attempt count come from the client itself, through <see cref="ScryClient.LiveActivity"/>, and
/// only if the app wired it with <see cref="ScrySidecarStore.Observe"/>. Either half alone is worth
/// showing, which is why neither is required.
/// </para>
/// <para>
/// Read while it is being written: the panel draws this on one thread while answers arrive on
/// another, so the lists it hands out are copies.
/// </para>
/// </remarks>
public sealed class ScrySidecarSession
{
    // Enough history to see a live query flapping, and a bound on what a live query left open for a
    // week can cost. Not an option: a session with more connections than this has already said
    // everything it has to say.
    const int maxConnections = 20;

    Lock sync = new();
    List<ScrySidecarConnection> connections = [];
    ScrySidecarOptions options;
    bool reported;

    internal ScrySidecarSession(long id, ScrySidecarOptions options)
    {
        Id = id;
        this.options = options;
        Started = DateTimeOffset.Now;
    }

    /// <summary>Identifies the live query for the life of the process.</summary>
    public long Id { get; }

    public DateTimeOffset Started { get; }

    /// <summary>Where the live query is now.</summary>
    /// <remarks>
    /// Inferred from the wire until the client reports one of its own, after which what the client
    /// says wins. The difference shows in one place: a gap between connections reads as
    /// <see cref="ScrySubscriptionState.Reconnecting"/> either way, but only the client knows
    /// whether it is really going to ask again.
    /// </remarks>
    public ScrySubscriptionState State { get; private set; } = ScrySubscriptionState.Connecting;

    /// <summary>
    /// Whether this sidecar saw the live query's connections. False for one carried somewhere it
    /// cannot watch — a hub connection, or a transport of the app's own — where what is known is
    /// what the client reported.
    /// </summary>
    public bool OnTheWire { get; private set; }

    /// <summary>Which connection the live query is on, counting from one.</summary>
    public int Attempt { get; private set; } = 1;

    public int Answers { get; private set; }

    public int Pings { get; private set; }

    /// <summary>
    /// How often the server answered that the answer already held is still the answer — on a
    /// reconnect that resumed cleanly.
    /// </summary>
    public int Unchanged { get; private set; }

    public DateTimeOffset? LastAnswer { get; private set; }

    /// <summary>
    /// When anything last arrived, heartbeats included. A live query that is idle but being pinged
    /// looks exactly like a hung one without this.
    /// </summary>
    public DateTimeOffset? LastEvent { get; private set; }

    /// <summary>What ended the live query, once it has failed.</summary>
    public string? Error { get; private set; }

    /// <summary>The most recent answer, pretty-printed, when it was small enough to keep.</summary>
    public string? LatestAnswer { get; private set; }

    /// <summary>
    /// Set when the log gave up on this session to stay within its ceiling. Stops the wire recording
    /// into a row nothing can reach any more.
    /// </summary>
    public bool Detached { get; internal set; }

    /// <summary>Whether the live query is still going.</summary>
    public bool Open =>
        !Detached &&
        State is ScrySubscriptionState.Connecting or ScrySubscriptionState.Live or ScrySubscriptionState.Reconnecting;

    /// <summary>Connections dropped to stay within the history this keeps.</summary>
    public int DroppedConnections { get; private set; }

    /// <summary>A snapshot of the connections this live query has been held open by, oldest first.</summary>
    public IReadOnlyList<ScrySidecarConnection> Connections
    {
        get
        {
            lock (sync)
            {
                return [.. connections];
            }
        }
    }

    /// <summary>The connection currently being read, if any.</summary>
    public ScrySidecarConnection? Current
    {
        get
        {
            lock (sync)
            {
                return connections.Count == 0 ? null : connections[^1];
            }
        }
    }

    internal ScrySidecarConnection Connect(string? resumedFrom)
    {
        lock (sync)
        {
            OnTheWire = true;
            var attempt = connections.Count + DroppedConnections + 1;
            var connection = new ScrySidecarConnection(attempt, resumedFrom, options.MaxSubscriptionEvents);

            // Counted from the connections where the client is not saying. Where it is, it has
            // already counted this one: it reports reconnecting before asking again.
            if (!reported)
            {
                Attempt = attempt;
            }

            // What an older connection answered is history the moment a newer one has answered too,
            // so only its most recent answer is kept.
            if (connections.Count > 0)
            {
                connections[^1].Trim(keep: 1);
            }

            connections.Add(connection);
            while (connections.Count > maxConnections)
            {
                connections.RemoveAt(0);
                DroppedConnections++;
            }

            return connection;
        }
    }

    internal void Observe(ScrySidecarConnection connection, ScrySidecarEvent captured)
    {
        lock (sync)
        {
            LastEvent = captured.At;
            switch (captured.Name)
            {
                case ScryLive.Result:
                    Answers++;
                    LastAnswer = captured.At;
                    LatestAnswer = captured.Json;
                    connection.Trim(options.MaxRetainedAnswers);
                    break;

                case ScryLive.Ping:
                    Pings++;
                    break;

                case ScryLive.Unchanged:
                    Unchanged++;
                    break;
            }

            // Anything arriving at all means the connection is answering. The client says so too,
            // but only where the app wired it, and this is true either way.
            if (!reported)
            {
                State = ScrySubscriptionState.Live;
            }
        }
    }

    internal void Close(ScrySidecarConnection connection, string ended, string? error, bool reconnect)
    {
        lock (sync)
        {
            connection.Ended = ended;
            connection.Duration = DateTimeOffset.Now - connection.Started;
            if (error is not null)
            {
                Error = error;
            }

            if (reported)
            {
                return;
            }

            // Without the client's own account, what happens next is read off how this connection
            // finished: a cut one is asked for again, and so is an ending the server said to come
            // back from.
            State = error is not null
                ? ScrySubscriptionState.Faulted
                : reconnect
                    ? ScrySubscriptionState.Reconnecting
                    : ScrySubscriptionState.Closed;
        }
    }

    /// <summary>
    /// What the client says about the live query, which is the whole account for one it carries
    /// somewhere this sidecar cannot watch, and the authoritative one everywhere else.
    /// </summary>
    internal void Report(ScrySubscriptionState state, int attempt, Exception? failure)
    {
        lock (sync)
        {
            reported = true;
            State = state;
            Attempt = attempt;
            if (failure is not null)
            {
                Error = failure.Message;
            }
        }
    }

    internal void Answered(string? json)
    {
        lock (sync)
        {
            // Only for a live query whose wire is not being watched; otherwise the events counted
            // it already, with a size and an identifier this cannot know.
            if (OnTheWire)
            {
                return;
            }

            Answers++;
            LastAnswer = DateTimeOffset.Now;
            LastEvent = LastAnswer;
            LatestAnswer = json;
        }
    }
}
