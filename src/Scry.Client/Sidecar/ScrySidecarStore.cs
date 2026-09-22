namespace Scry;

/// <summary>
/// Holds the exchanges the sidecar has captured. A singleton, deliberately apart from
/// <see cref="ScrySidecarHandler"/>: the HTTP factory rotates handlers, and a log that rotated with
/// them would forget everything every couple of minutes.
/// </summary>
public sealed class ScrySidecarStore(ScrySidecarOptions options)
{
    Lock sync = new();
    List<ScrySidecarEntry> entries = [];
    Dictionary<long, ScrySidecarSession> sessions = [];
    Dictionary<Guid, ScrySidecarCommand> commands = [];
    int nextId;

    /// <summary>Raised after an entry is added or the log is cleared.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when a live query has something new to show without the list of entries having
    /// changed. Apart from <see cref="Changed"/> because it fires as often as a server answers: a
    /// panel redrawing wants these coalesced, and a subscriber mirroring the log does not want them
    /// at all.
    /// </summary>
    public event Action? SessionChanged;

    /// <summary>A snapshot of the captured entries, oldest first.</summary>
    public IReadOnlyList<ScrySidecarEntry> Entries
    {
        get
        {
            lock (sync)
            {
                return [.. entries];
            }
        }
    }

    internal ScrySidecarEntry Add(ScrySidecarEntry entry)
    {
        lock (sync)
        {
            entry = entry with {Id = ++nextId};
            entries.Add(entry);
            Evict();
        }

        Changed?.Invoke();
        return entry;
    }

    internal void Touch() =>
        SessionChanged?.Invoke();

    /// <summary>
    /// The session a live query's connection belongs to, and a connection on it. The first
    /// connection of a live query this has not heard of adds the row; the ones that follow it are
    /// folded into that row, which is what makes a reconnect a reconnect rather than a second live
    /// query.
    /// </summary>
    internal (ScrySidecarSession Session, ScrySidecarConnection Connection) OpenLive(
        long id,
        ScrySidecarEntry entry,
        string? resumedFrom)
    {
        bool added;
        ScrySidecarSession session;
        lock (sync)
        {
            added = !sessions.ContainsKey(id);
            session = Session(id, entry);
            if (!added)
            {
                Upgrade(session, entry);
            }
        }

        if (added)
        {
            Changed?.Invoke();
        }

        var connection = session.Connect(resumedFrom);
        Touch();
        return (session, connection);
    }

    // Under the lock. A client that reports its live queries does so before the first connection
    // reaches the wire, so the row it added names no request — this one does, and replaces it.
    void Upgrade(ScrySidecarSession session, ScrySidecarEntry seen)
    {
        if (session.OnTheWire)
        {
            return;
        }

        var index = entries.FindIndex(_ => ReferenceEquals(_.Session, session));
        if (index >= 0)
        {
            entries[index] = seen with {Id = entries[index].Id, Session = session};
        }
    }

    /// <summary>
    /// The session a live query reported itself under, adding a row for it where nothing has been
    /// seen on the wire — which is every live query carried somewhere this sidecar cannot watch.
    /// </summary>
    internal ScrySidecarSession Live(long id, QueryRequest request)
    {
        bool added;
        ScrySidecarSession session;
        lock (sync)
        {
            added = !sessions.ContainsKey(id);
            session = Session(
                id,
                new()
                {
                    Id = 0,
                    Started = DateTimeOffset.Now,
                    Duration = TimeSpan.Zero,

                    // No request of its own: this live query is being reported rather than watched.
                    Method = "LIVE",
                    Url = "",
                    Kind = ScrySidecarKind.Subscription,
                    Request = request,
                    RequestJson = SidecarJson.Prettify(ScryJson.Serialize(request)),
                    RequestHeaders = []
                });
        }

        if (added)
        {
            Changed?.Invoke();
        }

        return session;
    }

    // Under the lock in every case.
    ScrySidecarSession Session(long id, ScrySidecarEntry entry)
    {
        if (sessions.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var session = new ScrySidecarSession(id, options);
        sessions[id] = session;
        entries.Add(entry with {Id = ++nextId, Session = session});
        Evict();
        return session;
    }

    /// <summary>
    /// Records a command's exchange into its row. Sending it adds the row, or takes over the one the
    /// client's report added a moment before; asking for it again folds into that row rather than
    /// adding one, as a live query's reconnect does.
    /// </summary>
    internal void CommandExchange(Guid id, string? name, bool reattach, ScrySidecarEntry seen, Action<ScrySidecarCommand> update)
    {
        lock (sync)
        {
            if (!commands.TryGetValue(id, out var command))
            {
                command = new(id, name ?? $"command {id:D}");
                commands[id] = command;
                entries.Add(seen with {Id = ++nextId, Command = command});
                Evict();
            }
            else if (reattach)
            {
                command.AskedAgain();
            }
            else if (!command.OnTheWire)
            {
                var index = entries.FindIndex(_ => ReferenceEquals(_.Command, command));
                if (index >= 0)
                {
                    entries[index] = seen with {Id = entries[index].Id, Command = command};
                }
            }

            command.OnTheWire = true;
            update(command);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The row a command reported itself under, adding one where nothing has been seen on the wire —
    /// which is every command sent somewhere this sidecar cannot watch, and every command reported
    /// before its exchange reaches the handler.
    /// </summary>
    ScrySidecarCommand ReportedCommand(ScryCommandActivity activity, out bool added)
    {
        lock (sync)
        {
            added = !commands.TryGetValue(activity.Id, out var command);
            if (command is null)
            {
                command = new(activity.Id, activity.Command);
                commands[activity.Id] = command;
                entries.Add(
                    new()
                    {
                        Id = ++nextId,
                        Started = DateTimeOffset.Now,
                        Duration = TimeSpan.Zero,

                        // No request of its own: this command is being reported rather than watched.
                        Method = "COMMAND",
                        Url = "",
                        Kind = ScrySidecarKind.Command,
                        RequestJson = SidecarJson.Prettify(ScryJson.Serialize(activity.Request)),
                        RequestHeaders = [],
                        Command = command
                    });
                Evict();
            }

            command.Report(activity);

            // With no exchange of its own to time, a reported command's row is timed from its send to
            // its outcome.
            if (!command.OnTheWire &&
                IsOutcome(activity.Kind))
            {
                var index = entries.FindIndex(_ => ReferenceEquals(_.Command, command));
                if (index >= 0)
                {
                    var entry = entries[index];
                    entries[index] = entry with {Duration = DateTimeOffset.Now - entry.Started};
                }
            }

            return command;
        }
    }

    static bool IsOutcome(ScryCommandActivityKind kind) =>
        kind is ScryCommandActivityKind.Refused or
            ScryCommandActivityKind.Completed or
            ScryCommandActivityKind.Failed or
            ScryCommandActivityKind.Unknown;

    /// <summary>
    /// Mirrors a client's live queries and commands into the log, so that the ones carried somewhere
    /// this sidecar cannot watch — a hub connection, or a transport of the app's own — are listed too,
    /// and the ones it can watch are labelled with what the client itself says about them.
    /// </summary>
    /// <remarks>
    /// Optional, and separate from <see cref="ScrySidecarServiceExtensions.AddScrySidecar"/> because
    /// the app builds its own <see cref="ScryClient"/>: registration has nothing to attach this to.
    /// Without it a live query over HTTP is still listed, still folds its reconnects into one row,
    /// and still shows every event — what is lost is the client's own account of the states between
    /// connections, and every live query that never touches HTTP.
    /// </remarks>
    public void Observe(ScryClient client)
    {
        client.LiveActivity += Report;
        client.CommandActivity += Report;
    }

    void Report(ScryCommandActivity activity)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            ReportedCommand(activity, out var added);
            if (added)
            {
                Changed?.Invoke();
                return;
            }

            Touch();
        }
        catch
        {
            // A debug log that cannot record has nothing useful to do about it.
        }
    }

    void Report(ScryLiveActivity activity)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            var session = Live(activity.Session, activity.Request);
            session.Report(activity.State, activity.Attempt, activity.Failure);
            if (activity.Answer is { } answer)
            {
                session.Answered(Kept(answer));
            }

            Touch();
        }
        catch
        {
            // A debug log that cannot record has nothing useful to do about it.
        }
    }

    string? Kept(QueryResponse answer)
    {
        try
        {
            var json = ScryJson.Serialize(answer);
            return json.Length > options.MaxRetainedAnswerBytes ? null : SidecarJson.Prettify(json);
        }
        catch
        {
            return null;
        }
    }

    // Under the lock in every case. The cap counts exchanges that are over: a live query is not
    // evicted while it is open, because its row is the one thing still being written to and a log
    // that forgot it would be worse than one slightly over its cap. Unless there are more open than
    // the cap allows twice over, at which point the oldest goes anyway and is detached — which is
    // what stops its connection recording into a row nothing can reach.
    void Evict()
    {
        while (entries.Count > options.MaxEntries)
        {
            var index = entries.FindIndex(_ => _.Session is not {Open: true});
            if (index < 0)
            {
                break;
            }

            Forget(entries[index]);
            entries.RemoveAt(index);
        }

        while (entries.Count > options.MaxEntries * 2)
        {
            Forget(entries[0]);
            entries.RemoveAt(0);
        }
    }

    void Forget(ScrySidecarEntry entry)
    {
        if (entry.Command is { } command)
        {
            commands.Remove(command.Id);
        }

        if (entry.Session is not { } session)
        {
            return;
        }

        session.Detached = true;
        sessions.Remove(session.Id);
    }

    public void Clear()
    {
        lock (sync)
        {
            foreach (var entry in entries)
            {
                Forget(entry);
            }

            entries.Clear();
        }

        Changed?.Invoke();
    }
}
