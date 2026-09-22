using Microsoft.Extensions.Hosting;

namespace Scry;

public sealed partial class ScryProcessor
{
    CommandTracker tracker = null!;
    CommandBinder binder = null!;

    void InitializeCommands()
    {
        tracker = new(options, schema.Stamp, Finished);
        binder = new(schema.Commands);
    }

    /// <summary>How many commands are in flight on this processor: accepted and not yet finished.</summary>
    public int PendingCommands => tracker.Pending;

    /// <summary>Sends a command without a service provider: its policies are constructed, and nothing is resolved.</summary>
    public IAsyncEnumerable<CommandReceipt> SendCommand(CommandRequest request, DbContext data, Cancel cancel = default) =>
        SendCommand(request, data, EmptyServiceProvider.Instance, new HeaderDictionary(), caller: null, cancel);

    /// <summary>
    /// Sends a command, answering with its receipts: the final one alone where it finished within
    /// <see cref="ScryOptions.CommandSyncWindow"/>, and otherwise a pending one at once and the final one
    /// when it lands. The programmatic form of the command endpoint, for a transport other than HTTP.
    /// </summary>
    /// <param name="request">The command: its name, the id the client gave it, and its payload.</param>
    /// <param name="data">The context the target is read through. Not the one a handler writes through.</param>
    /// <param name="services">
    /// What policies and auditors are resolved from, and where an in-process handler's own scope is made.
    /// </param>
    /// <param name="requestHeaders">Exposed to the command's policy and the target's row policies.</param>
    /// <param name="caller">
    /// Who is asking — the authenticated identity, never something the caller supplied. Counted against
    /// <see cref="ScryOptions.MaxPendingCommandsPerCaller"/>, handed to the handler, and the only caller a
    /// pending command's outcome is given to again.
    /// </param>
    /// <param name="cancel">Stops waiting. Never stops the command, which is its handler's once accepted.</param>
    /// <remarks>
    /// Everything a command can be refused for throws from the first <c>MoveNextAsync</c>, before anything
    /// is dispatched — a malformed payload or unknown command as <see cref="ScryValidationException"/>, a
    /// caller the policy refuses outright as <see cref="ScryPermissionException"/>, a target that is not
    /// there for the caller as <see cref="ScryCommandNotFoundException"/>, one command too many as
    /// <see cref="ScryCommandLimitException"/> — so a transport can answer each with a status.
    /// </remarks>
    public async IAsyncEnumerable<CommandReceipt> SendCommand(
        CommandRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        string? caller = null,
        [EnumeratorCancellation] Cancel cancel = default)
    {
        var started = Stopwatch.GetTimestamp();
        var activity = CommandRecorder.Start(request.Command);
        CommandRecord record;
        try
        {
            record = await Accept(request, data, services, requestHeaders, caller, cancel);
        }
        catch (Exception exception)
        {
            Refused(request, exception, started, activity, services);
            throw;
        }

        var first = await FirstReceipt(record, services, cancel);
        var outcome = first.Status switch
        {
            CommandStatus.Completed => "completed",
            CommandStatus.Failed => "failed",
            _ => "pending"
        };
        CommandRecorder.Answered(activity, request.Command, outcome, Stopwatch.GetElapsedTime(started), first.Error);
        Audit(services, request, first.Status == CommandStatus.Failed ? ScryQueryOutcome.Failed : ScryQueryOutcome.Success, first.Status, Stopwatch.GetElapsedTime(started), Error(record, first), staleClient: false);

        yield return first;
        if (first.Status != CommandStatus.Pending)
        {
            yield break;
        }

        yield return await record.Completion.WaitAsync(cancel);
    }

    /// <summary>
    /// A command already sent, asked for again by its id — after a stream was cut, or ended to bound its
    /// lifetime. Answers as <see cref="SendCommand(CommandRequest, DbContext, IServiceProvider, IHeaderDictionary, string?, Cancel)"/>
    /// does: the final receipt alone, or a pending one and then the final one.
    /// </summary>
    /// <remarks>
    /// Only for the caller that sent it: anyone else — and an id this node does not hold, whether it never
    /// was, was pruned, or was sent to another node — is answered alike, with
    /// <see cref="ScryCommandNotFoundException"/>.
    /// </remarks>
    public async IAsyncEnumerable<CommandReceipt> Receipt(Guid id, string? caller = null, [EnumeratorCancellation] Cancel cancel = default)
    {
        var record = tracker.Find(id, caller) ??
                     throw new ScryCommandNotFoundException(ScryCommandNotFoundException.CommandMessage);
        var current = record.Current;
        yield return current;
        if (current.Status != CommandStatus.Pending)
        {
            yield break;
        }

        yield return await record.Completion.WaitAsync(cancel);
    }

    /// <summary>
    /// The commands this caller may send at all: every command this host serves whose policy allows
    /// them. Advisory — each command is decided again when it is sent.
    /// </summary>
    public CommandCapabilities Capabilities(DbContext data, IServiceProvider services, IHeaderDictionary requestHeaders)
    {
        // Commands off: nothing is served, so there is nothing to send.
        if (options.MaxPendingCommands <= 0)
        {
            return CommandCapabilities.Create([], schema.Stamp);
        }

        var context = new ScryPolicyContext(services, data, requestHeaders, new HeaderDictionary());
        var allowed = schema.Commands
            .Where(_ => _.Available &&
                        (_.Policy is null || CommandPolicy.Allow(_.Policy, _.ClrType, context)))
            .Select(_ => _.Name)
            .ToList();
        return CommandCapabilities.Create(allowed, schema.Stamp);
    }

    /// <summary>
    /// Reports that a command finished. What a dispatcher calls when the other end says the handler ran;
    /// the in-process one calls it too. False where the command is not one this processor holds in
    /// flight — unknown, already finished, or pruned — which a redelivered completion is.
    /// </summary>
    /// <param name="id">The command's id, as the envelope carried it.</param>
    /// <param name="result">What the handler answered with, as JSON, or null for a command with no result.</param>
    public bool CompleteCommand(Guid id, JsonElement? result = null) =>
        tracker.Finish(
            id,
            CommandReceipt.Create(id, CommandStatus.Completed) with
            {
                Result = result
            }) is not null;

    /// <summary>
    /// Reports that a command failed, with the message its sender is shown. Say only what a caller may
    /// read — the real failure belongs in a log — since this is exactly what reaches the client.
    /// </summary>
    public bool FailCommand(Guid id, string message) =>
        Fail(id, message, failure: null);

    bool Fail(Guid id, string message, Exception? failure)
    {
        var shown = message.Length <= ScryCommandException.MaxMessageLength
            ? message
            : string.Concat(message.AsSpan(0, ScryCommandException.MaxMessageLength), "…");
        return tracker.Finish(
            id,
            CommandReceipt.Create(id, CommandStatus.Failed) with
            {
                Error = shown
            },
            failure) is not null;
    }

    // Everything before a command is in flight: read, bound, authorized, its target found, accepted and
    // handed on. Throws for every refusal, which the caller records.
    async Task<CommandRecord> Accept(
        CommandRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        string? caller,
        Cancel cancel)
    {
        if (options.MaxPendingCommands <= 0)
        {
            throw new InvalidOperationException($"Commands are off: ScryOptions.{nameof(options.MaxPendingCommands)} is zero. Set it to how many commands this server may have in flight at once.");
        }

        var drifted = request.Stamp is { } stamp && stamp != schema.Stamp;
        try
        {
            if (request.Version is < 1 or > CommandRequest.CurrentVersion)
            {
                throw new ScryValidationException($"Unsupported command request version {request.Version}; this server supports up to {CommandRequest.CurrentVersion}.");
            }

            if (!schema.TryGetCommand(request.Command, out var meta))
            {
                throw new ScryValidationException($"Unknown command '{request.Command}'.");
            }

            if (!meta.Available)
            {
                throw new ScryValidationException($"Command '{meta.Name}' is not available on this server.");
            }

            var command = binder.Bind(meta, request.Payload);
            var context = new ScryPolicyContext(services, data, requestHeaders, new HeaderDictionary());
            if (meta.Policy is { } policy &&
                !CommandPolicy.Allow(policy, meta.ClrType, context))
            {
                throw new ScryPermissionException(ScryPermissionException.CommandDeniedMessage);
            }

            IReadOnlyList<object> keys = [];
            if (meta.Target is not null)
            {
                var values = meta.TargetKeys.Select(_ => _.Payload.GetValue(command)).ToList();
                var rows = meta.Policy is { } rowsPolicy ? CommandPolicy.Rows(rowsPolicy, meta.ClrType, context) : null;
                var scope = new CallScope(services, requestHeaders, new HeaderDictionary());

                // One query and one answer: a row that is not there, one a policy hides, and one the
                // command's own policy denies are all the same not-found, so a caller probing keys learns
                // nothing about rows it may not see.
                if (values.Any(_ => _ is null) ||
                    !await executor.CommandTargetExistsAsync(meta, values!, rows, data, scope, cancel))
                {
                    throw new ScryCommandNotFoundException(ScryCommandNotFoundException.TargetMessage);
                }

                keys = values!;
            }

            var record = tracker.Accept(request, meta, caller);
            var envelope = new CommandEnvelope(request.Id, meta.Name, meta.ClrType, command, caller, meta.Target?.Name, keys, meta.Result);
            try
            {
                await Dispatch(meta, envelope, services);
            }
            catch (Exception exception)
            {
                // Accepted and not delivered: it is answered as the failure it is, with the real reason
                // kept for the audit trail rather than shown.
                Fail(request.Id, "Command dispatch failed.", exception);
            }

            return record;
        }
        catch (ScryValidationException exception) when (drifted)
        {
            throw new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                StaleClient = true
            };
        }
    }

    // The receipt a command is first answered with: its outcome where it finished inside the sync
    // window, pending where it did not — marked so, under the tracker's lock, so a command finishing in
    // the same instant is answered as finished rather than pending with nobody auditing its end.
    async Task<CommandReceipt> FirstReceipt(CommandRecord record, IServiceProvider services, Cancel cancel)
    {
        var completion = record.Completion;
        if (!completion.IsCompleted &&
            options.CommandSyncWindow > TimeSpan.Zero)
        {
            try
            {
                await completion.WaitAsync(options.CommandSyncWindow, cancel);
            }
            catch (TimeoutException)
            {
                // Still running: answered as pending below.
            }
        }

        if (completion.IsCompleted ||
            !tracker.AnswerPending(record, services.GetService<IServiceScopeFactory>()))
        {
            return await completion;
        }

        return record.Current;
    }

    async Task Dispatch(CommandMeta meta, CommandEnvelope envelope, IServiceProvider services)
    {
        var stopping = Stopping(services);
        foreach (var type in options.Dispatchers)
        {
            var dispatcher = (ICommandDispatcher) (services.GetService(type) ??
                                                   throw new($"Dispatcher '{type.Name}' was added with ScryOptions.AddDispatcher but is not registered with the service provider."));
            if (dispatcher.CanDispatch(meta.ClrType))
            {
                await dispatcher.Dispatch(envelope, stopping);
                return;
            }
        }

        var scopes = services.GetService<IServiceScopeFactory>() ??
                     throw new($"Command '{meta.Name}' is handled in-process, which needs a service provider to make its scope from, and none was given.");

        // On the pool, with a scope and a context of its own: the request that sent the command may have
        // finished long before the handler does, and its scope with it.
        _ = Task.Run(() => HandleInProcess(meta, envelope, scopes, stopping), Cancel.None);
    }

    async Task HandleInProcess(CommandMeta meta, CommandEnvelope envelope, IServiceScopeFactory scopes, Cancel stopping)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var db = (DbContext) provider.GetRequiredService(options.ContextType);
            var context = new ScryCommandContext(provider, db, envelope.Id, envelope.Name, envelope.Caller, envelope.Keys);
            var invoker = HandlerInvoker.For(meta);
            var handler = provider.GetService(invoker.HandlerType) ??
                          throw new($"No {HandlerName(meta)} is registered for command '{meta.Name}'.");
            var result = await invoker.Invoke(handler, envelope.Command, context, stopping);
            if (context.SaveChanges &&
                db.ChangeTracker.HasChanges())
            {
                await db.SaveChangesAsync(stopping);
            }

            JsonElement? serialized = null;
            if (meta.Result is { } resultType &&
                result is not null)
            {
                serialized = JsonSerializer.SerializeToElement(result, resultType, ScryJson.Options);
            }

            CompleteCommand(envelope.Id, serialized);
        }
        catch (ScryCommandException exception)
        {
            Fail(envelope.Id, exception.Message, exception);
        }
        catch (OperationCanceledException exception) when (stopping.IsCancellationRequested)
        {
            Fail(envelope.Id, "The server stopped before the command finished.", exception);
        }
        catch (Exception exception)
        {
            Fail(envelope.Id, "Command execution failed.", exception);
        }
    }

    static Cancel Stopping(IServiceProvider services) =>
        services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? Cancel.None;

    static string HandlerName(CommandMeta meta)
    {
        if (meta.Result is { } result)
        {
            return $"ICommandHandler<{meta.ClrType.Name}, {result.Name}>";
        }

        return $"ICommandHandler<{meta.ClrType.Name}>";
    }

    // Called by the tracker as each command finishes, however it finished: the metric, the change
    // a completed targeted command reports, and — for one answered as pending — its second audit entry.
    void Finished(CommandRecord record)
    {
        var status = record.Current.Status;
        CommandRecorder.Handled(record.Meta.Name, status, Stopwatch.GetElapsedTime(record.AcceptedTimestamp));

        // The handler's save was reported already where its context carries the interceptor. This
        // covers the rest — an ExecuteDelete, a worker that reports nothing — and costs no second run
        // where both land: a live query's due flag is a flag, not a queue.
        if (status == CommandStatus.Completed &&
            record.Meta.Target is { } target)
        {
            Changes.Notify(target.ClrType);
        }

        if (record.AuditScopes is { } scopes)
        {
            using var scope = scopes.CreateScope();
            Audit(
                scope.ServiceProvider,
                record.Request,
                status == CommandStatus.Completed ? ScryQueryOutcome.Success : ScryQueryOutcome.Failed,
                status,
                Stopwatch.GetElapsedTime(record.AcceptedTimestamp),
                Error(record, record.Current),
                staleClient: false);
        }
    }

    // What the trail records as the failure: the real one where the client was shown a fixed message.
    static string? Error(CommandRecord record, CommandReceipt receipt)
    {
        if (receipt.Status != CommandStatus.Failed)
        {
            return null;
        }

        return record.Failure?.Message ?? receipt.Error;
    }

    static void Refused(CommandRequest request, Exception exception, long started, Activity? activity, IServiceProvider services)
    {
        var (outcome, audited) = exception switch
        {
            ScryValidationException => ("rejected", ScryQueryOutcome.Rejected),
            ScryPermissionException => ("denied", ScryQueryOutcome.Denied),
            ScryCommandNotFoundException => ("not_found", ScryQueryOutcome.Rejected),
            ScryCommandLimitException => ("limited", ScryQueryOutcome.Rejected),
            OperationCanceledException => ("canceled", ScryQueryOutcome.Canceled),
            _ => ("failed", ScryQueryOutcome.Failed)
        };
        var elapsed = Stopwatch.GetElapsedTime(started);
        CommandRecorder.Answered(activity, request.Command, outcome, elapsed, exception.Message);
        Audit(services, request, audited, status: null, elapsed, exception.Message, staleClient: exception is ScryValidationException {StaleClient: true});
    }

    // Auditors are resolved per command, as they are per query, so a scoped one can read the caller.
    static void Audit(
        IServiceProvider services,
        CommandRequest request,
        ScryQueryOutcome outcome,
        CommandStatus? status,
        TimeSpan elapsed,
        string? error,
        bool staleClient)
    {
        if (services.GetService<IEnumerable<IScryAuditor>>() is not { } auditors)
        {
            return;
        }

        ScryAuditEntry? entry = null;
        foreach (var auditor in auditors)
        {
            entry ??= new(null, outcome, elapsed)
            {
                Command = request,
                CommandStatus = status,
                Error = error,
                StaleClient = staleClient
            };
            auditor.Record(entry);
        }
    }

    /// <summary>
    /// Confirms every command this host serves has somewhere to go — one dispatcher claiming it, or an
    /// in-process handler of the arity its result calls for — throwing a directed error for the first
    /// that does not. Checks nothing where <see cref="ScryOptions.MaxPendingCommands"/> is zero, which
    /// serves no command. Part of <see cref="EnsureReady"/>.
    /// </summary>
    public void EnsureCommandsDispatchable(IServiceProvider services)
    {
        if (options.MaxPendingCommands <= 0)
        {
            return;
        }

        var dispatchers = options.Dispatchers
            .Select(type => (ICommandDispatcher) (services.GetService(type) ??
                                                  throw new($"Dispatcher '{type.Name}' was added with ScryOptions.AddDispatcher but is not registered with the service provider. Register it, or remove it.")))
            .ToList();
        foreach (var command in schema.Commands.Where(_ => _.Available))
        {
            var claiming = dispatchers.Where(_ => _.CanDispatch(command.ClrType)).ToList();
            if (claiming.Count > 1)
            {
                throw new($"Command '{command.Name}' is claimed by {string.Join(" and ", claiming.Select(_ => _.GetType().Name))}. A command goes one way: tell all but one of them to leave it.");
            }

            if (claiming.Count == 1 ||
                services.GetService(HandlerInvoker.For(command).HandlerType) is not null)
            {
                continue;
            }

            throw new($"Command '{command.Name}' has nowhere to go: no dispatcher claims it and no {HandlerName(command)} is registered. Register one — services.AddScoped<{HandlerName(command)}, ...>() — or a dispatcher that claims it, or leave commands off.");
        }
    }

    /// <summary>
    /// Marks a command unavailable where its target is a source the context does not map — which
    /// <see cref="EnsureSourcesMapped"/> allows only under <see cref="ScryOptions.AllowUnmappedSources"/>.
    /// Such a command is refused when sent, and its capability reads false. Part of <see cref="EnsureReady"/>.
    /// </summary>
    public void EnsureCommandTargetsMapped(DbContext data)
    {
        foreach (var command in schema.Commands)
        {
            if (command.Target is { } target &&
                data.Model.FindEntityType(target.ClrType) is null)
            {
                command.Available = false;
            }
        }
    }

    /// <summary>
    /// Translates every command policy's row condition once, so one EF cannot translate fails the
    /// deployment rather than the first query projecting its capability. Part of <see cref="EnsureReady"/>
    /// where <see cref="ScryOptions.ProbePoliciedNavigations"/> is on.
    /// </summary>
    public void ProbeCommandPolicies(DbContext data, IServiceProvider services)
    {
        var context = new ScryPolicyContext(services, data);
        foreach (var command in schema.Commands)
        {
            if (command is not {Available: true, Policy: { } policy, PolicyRows: not null} ||
                CommandPolicy.Rows(policy, command.ClrType, context) is not { } rows)
            {
                continue;
            }

            try
            {
                QueryExecutor.ProbeCommandRows(command, rows, data, services);
            }
            catch (Exception exception)
            {
                throw new($"Command policy '{policy.Name}' answers '{command.Name}' with rows EF cannot translate: {exception.Message}", exception);
            }
        }
    }
}
