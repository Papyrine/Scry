/// <summary>
/// The commands a processor has accepted: which are in flight, whose each is, and the outcomes of those
/// that finished, kept for <see cref="ScryOptions.CommandRetention"/> so a client whose stream was cut
/// can ask again. In memory, and so this node's alone — a client asking a different node is told the
/// command is not one it holds.
/// </summary>
sealed class CommandTracker(ScryOptions options, string stamp, Action<CommandRecord> finished)
{
    Lock gate = new();
    Dictionary<Guid, CommandRecord> records = [];
    Dictionary<string, int> callers = new(StringComparer.Ordinal);
    int pending;
    bool pruning;

    /// <summary>How many commands are in flight.</summary>
    public int Pending
    {
        get
        {
            lock (gate)
            {
                return pending;
            }
        }
    }

    /// <summary>
    /// Records a command as accepted and in flight, or refuses it: an id already held, or one command
    /// more than the server, or this caller, may have in flight.
    /// </summary>
    public CommandRecord Accept(CommandRequest request, CommandMeta meta, string? caller)
    {
        CommandRecord record;
        lock (gate)
        {
            if (records.ContainsKey(request.Id))
            {
                throw new ScryValidationException($"A command with id '{request.Id:D}' has already been sent. Every command needs an id of its own.");
            }

            if (pending >= options.MaxPendingCommands)
            {
                throw new ScryCommandLimitException(
                    "This server has as many commands in flight as it allows. Send the command again shortly.",
                    perCaller: false);
            }

            if (caller is not null)
            {
                var held = callers.GetValueOrDefault(caller);
                if (held >= options.MaxPendingCommandsPerCaller)
                {
                    throw new ScryCommandLimitException(
                        "This caller has as many commands in flight as it is allowed. Send the command again once one has finished.",
                        perCaller: true);
                }

                callers[caller] = held + 1;
            }

            record = new(request, meta, caller, DateTimeOffset.UtcNow, stamp);
            records[request.Id] = record;
            pending++;
            StartPruning();
        }

        CommandRecorder.Accepted();
        return record;
    }

    /// <summary>
    /// Marks a command as answered pending — its end is then audited as an entry of its own, from a
    /// scope made by <paramref name="scopes"/> — or answers false where it has already finished, and so
    /// is to be answered with its outcome instead.
    /// </summary>
    public bool AnswerPending(CommandRecord record, IServiceScopeFactory? scopes)
    {
        lock (gate)
        {
            if (record.Finished is not null)
            {
                return false;
            }

            record.AuditScopes = scopes;
            return true;
        }
    }

    /// <summary>
    /// Finishes a command with its outcome, or answers null where it is not one this tracker holds in
    /// flight: unknown, already finished, or pruned. A command finishes once; a second report of it —
    /// a bus redelivering a completion — changes nothing.
    /// </summary>
    public CommandRecord? Finish(Guid id, CommandReceipt receipt, Exception? failure = null)
    {
        CommandRecord? record;
        lock (gate)
        {
            if (!records.TryGetValue(id, out record) ||
                record.Finished is not null)
            {
                return null;
            }

            record.Finish(receipt with {Stamp = stamp}, DateTimeOffset.UtcNow, failure);
            Released(record);
        }

        CommandRecorder.Released();
        try
        {
            finished(record);
        }
        finally
        {
            record.Release();
        }

        return record;
    }

    /// <summary>A command this tracker holds, for the caller that sent it; null for anyone else.</summary>
    public CommandRecord? Find(Guid id, string? caller)
    {
        lock (gate)
        {
            if (records.TryGetValue(id, out var record) &&
                record.Caller == caller)
            {
                return record;
            }

            return null;
        }
    }

    // Called under the gate.
    void Released(CommandRecord record)
    {
        pending--;
        if (record.Caller is not { } caller)
        {
            return;
        }

        var held = callers.GetValueOrDefault(caller) - 1;
        if (held > 0)
        {
            callers[caller] = held;
        }
        else
        {
            callers.Remove(caller);
        }
    }

    /// <summary>
    /// Forgets the outcomes kept past their retention, and fails the commands still pending long past
    /// it — a handler or a bus that never answered must not hold a caller's place for ever.
    /// </summary>
    internal void Prune(DateTimeOffset now)
    {
        List<CommandRecord> abandoned = [];
        lock (gate)
        {
            foreach (var record in records.Values.ToList())
            {
                if (record.Finished is { } at)
                {
                    if (now - at > options.CommandRetention)
                    {
                        records.Remove(record.Id);
                    }

                    continue;
                }

                if (now - record.Accepted > options.CommandRetention * 12)
                {
                    abandoned.Add(record);
                }
            }
        }

        foreach (var record in abandoned)
        {
            Finish(
                record.Id,
                CommandReceipt.Create(record.Id, CommandStatus.Failed) with
                {
                    Error = "No completion was received."
                });
        }
    }

    // Called under the gate. One loop for the life of the processor, started by the first command.
    void StartPruning()
    {
        if (pruning)
        {
            return;
        }

        pruning = true;
        _ = PruneLoop();
    }

    async Task PruneLoop()
    {
        var interval = options.CommandRetention < TimeSpan.FromMinutes(1) ? options.CommandRetention : TimeSpan.FromMinutes(1);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                Prune(DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
            {
                QueryRecorder.SignalFailed("command-prune", exception);
            }
        }
    }
}
