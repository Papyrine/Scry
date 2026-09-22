using System.Collections.Frozen;
using System.Net.ServerSentEvents;

namespace Scry;

public sealed partial class ScryClient :
    IAsyncDisposable,
    IDisposable
{
    Func<CommandRequest, Cancel, IAsyncEnumerable<CommandReceipt>>? commandTransport;
    Func<Guid, Cancel, IAsyncEnumerable<CommandReceipt>>? receiptTransport;
    Func<Cancel, Task<CommandCapabilities?>>? capabilitiesTransport;

    CancelSource disposing = new();
    ConcurrentDictionary<Guid, Task> following = new();
    Lock capabilitiesSync = new();
    TaskCompletionSource? firstCapabilities;
    FrozenSet<string>? capabilities;
    TimeSpan commandWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long <see cref="SendCommandAsync{TCommand}"/> waits for a command's outcome before answering
    /// it as <see cref="ScryCommandStatus.Pending"/> and listing it in <see cref="PendingWork"/>. Three
    /// seconds unless set; <see cref="Timeout.InfiniteTimeSpan"/> waits for the outcome however long it
    /// takes.
    /// </summary>
    /// <remarks>
    /// The server answers at once where it decides a command within its own sync window, so what this
    /// bounds is the wait for one it did not: long enough that a command which is merely slow still
    /// reads as synchronous, short enough that a screen is not held up by one that is queued.
    /// </remarks>
    public TimeSpan CommandWait
    {
        get => commandWait;
        set
        {
            if (value < TimeSpan.Zero &&
                value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "CommandWait must not be negative, other than Timeout.InfiniteTimeSpan.");
            }

            commandWait = value;
        }
    }

    /// <summary>The commands this client stopped waiting for while the server was still handling them.</summary>
    public ScryPendingWorkStore PendingWork { get; } = new();

    /// <summary>
    /// Raised as this client's commands are sent, refused, answered as pending, asked for again and
    /// finished. For watching rather than driving: the debug sidecar lists commands from it, and an app
    /// could log it. A handler that throws is swallowed.
    /// </summary>
    public event Action<ScryCommandActivity>? CommandActivity;

    /// <summary>
    /// Raised when what the server says this caller may send changes — including the first time it
    /// says anything. Raised on the <see cref="SynchronizationContext"/> the read was started on, where
    /// there was one, so a component can re-render from the handler.
    /// </summary>
    public event Action? CapabilitiesChanged;

    /// <summary>
    /// Whether this caller may send <paramref name="command"/> at all, as the server last said. Advisory:
    /// the server decides again on every command, and a targeted command's rows are decided apart from
    /// this, by the <c>Can{Command}</c> member its target's query model carries.
    /// </summary>
    /// <remarks>
    /// False until the server has answered — the first call starts that read — and false where the
    /// server serves no commands or could not be asked. Never throws. <see cref="CapabilitiesChanged"/>
    /// says when the answer moves, and <see cref="Ready"/> when the first one is in.
    /// </remarks>
    public bool Can(string command)
    {
        if (capabilities is { } known)
        {
            return known.Contains(command);
        }

        _ = Ready;
        return false;
    }

    /// <summary>
    /// Completes once the server has first said which commands this caller may send, or could not be
    /// asked. Never faults. Reading it starts that read, as calling <see cref="Can"/> does.
    /// </summary>
    public Task Ready
    {
        get
        {
            TaskCompletionSource started;
            lock (capabilitiesSync)
            {
                if (firstCapabilities is { } existing)
                {
                    return existing.Task;
                }

                firstCapabilities = started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // Outside the lock: a transport that answers synchronously raises CapabilitiesChanged from
            // here, and a handler reading Ready again must find it set rather than wait on itself.
            _ = FirstCapabilities(started, SynchronizationContext.Current);
            return started.Task;
        }
    }

    async Task FirstCapabilities(TaskCompletionSource done, SynchronizationContext? context)
    {
        try
        {
            await ReadCapabilities(context, disposing.Token);
        }
        catch
        {
            // Disposed before the server answered. Can goes on answering false.
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>
    /// Asks the server again which commands this caller may send, raising <see cref="CapabilitiesChanged"/>
    /// where the answer moved. Done for you after a command is denied. A server that cannot be asked
    /// leaves what was known in place.
    /// </summary>
    public Task RefreshCapabilitiesAsync(Cancel cancel = default) =>
        ReadCapabilities(SynchronizationContext.Current, cancel);

    async Task ReadCapabilities(SynchronizationContext? context, Cancel cancel)
    {
        if (capabilitiesTransport is not { } transport)
        {
            SetCapabilities([], context);
            return;
        }

        CommandCapabilities? read;
        try
        {
            read = await transport(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Advisory: failing to read it leaves what was known — or nothing — as it was.
            return;
        }

        if (read?.Stamp is { } stamp)
        {
            RecordServerStamp(stamp);
        }

        // No route at all is a server with commands off, which allows nothing.
        SetCapabilities(read?.Commands ?? [], context);
    }

    void SetCapabilities(IReadOnlyCollection<string> commands, SynchronizationContext? context)
    {
        var next = commands.ToFrozenSet(StringComparer.Ordinal);
        var previous = Interlocked.Exchange(ref capabilities, next);
        if (previous is not null &&
            previous.SetEquals(next))
        {
            return;
        }

        if (context is null)
        {
            RaiseCapabilitiesChanged();
            return;
        }

        context.Post(_ => RaiseCapabilitiesChanged(), null);
    }

    void RaiseCapabilitiesChanged()
    {
        try
        {
            CapabilitiesChanged?.Invoke();
        }
        catch
        {
            // A component that throws while redrawing must not stop the next read being taken.
        }
    }

    // begin-snippet: sendCommandApi
    /// <summary>
    /// Sends a command, answering with its outcome where the server decides it within
    /// <see cref="CommandWait"/> and otherwise as <see cref="ScryCommandStatus.Pending"/>, listed in
    /// <see cref="PendingWork"/> until its outcome arrives on <see cref="ScryCommandOutcome.Completion"/>.
    /// </summary>
    /// <param name="command">A generated command class, which names the command it is.</param>
    /// <param name="cancel">
    /// Stops waiting. Before the server has answered it also abandons the request, so the command may
    /// or may not have been accepted; after that it never stops the command, which goes on being
    /// followed in <see cref="PendingWork"/>.
    /// </param>
    /// <remarks>
    /// Throws for a command the server would not accept — malformed, denied, one too many — as a query
    /// the server refuses does: it never ran, so sending it again is safe where the refusal was about
    /// the moment rather than the request. A target that was gone by the time the command arrived is
    /// not a refusal but a <see cref="ScryCommandStatus.Failed"/> outcome: a race lost to whoever
    /// removed it.
    /// </remarks>
    public Task<ScryCommandOutcome> SendCommandAsync<TCommand>(TCommand command, Cancel cancel = default)
        where TCommand : class =>
        SendCommand(command, cancel);

    /// <summary>
    /// Sends a command that answers with a result, as <see cref="SendCommandAsync{TCommand}"/> does, with
    /// the result typed on <see cref="ScryCommandOutcome{TResult}.Value"/>.
    /// </summary>
    public async Task<ScryCommandOutcome<TResult>> SendCommandAsync<TCommand, TResult>(TCommand command, Cancel cancel = default)
        where TCommand : class =>
        ScryCommandOutcome<TResult>.From(await SendCommand(command, cancel));
    // end-snippet

    async Task<ScryCommandOutcome> SendCommand(object command, Cancel cancel)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(disposing.IsCancellationRequested, this);
        var type = command.GetType();
        var model = ScryCommandModels.Of(type);
        if (commandTransport is not { } transport)
        {
            throw new NotSupportedException(
                """
                This client's transport does not send commands.
                Construct the client with a command transport (ScryClient.ForHttp does).
                """);
        }

        var request = CommandRequest.Create(
            model.Name,
            Guid.NewGuid(),
            JsonSerializer.SerializeToElement(command, type, ScryJson.Options),
            SchemaStamp);
        var started = Stopwatch.GetTimestamp();
        var sent = DateTimeOffset.Now;
        var context = SynchronizationContext.Current;
        var reading = CancelSource.CreateLinkedTokenSource(disposing.Token);
        ReportCommand(request, ScryCommandActivityKind.Sent, attempt: 1);

        IAsyncEnumerator<CommandReceipt>? receipts = null;
        CommandReceipt first;
        try
        {
            receipts = transport(request, reading.Token).GetAsyncEnumerator(reading.Token);
            first = await First(receipts, reading, cancel);
        }
        catch (Exception exception)
        {
            if (receipts is not null)
            {
                await receipts.DisposeAsync();
            }

            reading.Dispose();
            if (TargetGone(request, exception) is { } gone)
            {
                return gone;
            }

            Refuse(request, exception, cancel);
            throw;
        }

        RecordReceipt(first);
        if (first.Status != CommandStatus.Pending)
        {
            await receipts.DisposeAsync();
            reading.Dispose();
            return Finished(request, first, attempt: 1);
        }

        ReportCommand(request, ScryCommandActivityKind.Pending, attempt: 1, receipt: first);
        var follower = Follow(request, receipts, reading);
        following[request.Id] = follower;

        // One that settled synchronously has already been through its own cleanup, so is not left behind.
        if (follower.IsCompleted)
        {
            following.TryRemove(request.Id, out _);
        }

        try
        {
            if (commandWait == Timeout.InfiniteTimeSpan)
            {
                return await follower.WaitAsync(cancel);
            }

            var remaining = commandWait - Stopwatch.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                return await follower.WaitAsync(remaining, cancel);
            }
        }
        catch (TimeoutException)
        {
            // Still being handled: answered as pending below.
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The caller stopped waiting and the command did not: it is followed where anyone can see.
            List(request, model, command, sent, follower, context);
            throw;
        }

        // Finished in the instant between the wait running out and here: an outcome in hand beats a
        // pending entry that completes as it is drawn.
        if (follower.IsCompleted)
        {
            return await follower;
        }

        var pending = List(request, model, command, sent, follower, context);
        return ScryCommandOutcome.InFlight(request.Command, request.Id, pending, follower);
    }

    // Until the server has answered, the caller's token is the command's too: nothing has been accepted
    // that abandoning the request would leave running. After this it stops only the waiting.
    static async Task<CommandReceipt> First(IAsyncEnumerator<CommandReceipt> receipts, CancelSource reading, Cancel cancel)
    {
        await using var abandoning = cancel.Register(static state => ((CancelSource) state!).Cancel(), reading);
        if (!await receipts.MoveNextAsync())
        {
            throw new ScryWireException("The command's connection ended before the server answered it, so whether it was accepted is unknown.");
        }

        return receipts.Current;
    }

    ScryPendingCommand List(
        CommandRequest request,
        ScryCommandAttribute model,
        object command,
        DateTimeOffset sent,
        Task<ScryCommandOutcome> follower,
        SynchronizationContext? context)
    {
        var pending = new ScryPendingCommand(request.Command, request.Id, model.Target, ScryCommandModels.Keys(command, model), sent, follower);
        PendingWork.Add(pending, context);
        _ = Unlist(pending, follower);
        return pending;
    }

    async Task Unlist(ScryPendingCommand pending, Task<ScryCommandOutcome> follower) =>
        PendingWork.Finish(pending, await follower.ConfigureAwait(false));

    // A target that was not there when the command arrived is a race lost to whoever removed it: an
    // outcome to show, where every other first-answer failure is a refusal of the request itself.
    ScryCommandOutcome? TargetGone(CommandRequest request, Exception exception)
    {
        if (exception is not ScryRequestException {Code: ScryErrorCode.NotFound} notFound)
        {
            return null;
        }

        ReportCommand(request, ScryCommandActivityKind.Failed, attempt: 1, failure: exception);
        return ScryCommandOutcome.Final(request.Command, request.Id, ScryCommandStatus.Failed, result: null, Shown(notFound));
    }

    // A refusal is thrown as a query's is; this says so first, and says it with the caller's own token
    // where it was the caller that stopped.
    void Refuse(CommandRequest request, Exception exception, Cancel cancel)
    {
        ReportCommand(request, ScryCommandActivityKind.Refused, attempt: 1, failure: exception);

        // What this caller may do has moved since it was last read, or the button would not have been
        // there to press.
        if (exception is ScryPermissionException)
        {
            _ = QuietlyRefreshCapabilities();
        }

        if (exception is OperationCanceledException)
        {
            cancel.ThrowIfCancellationRequested();
        }
    }

    async Task QuietlyRefreshCapabilities()
    {
        try
        {
            await ReadCapabilities(context: null, disposing.Token);
        }
        catch
        {
            // Disposed meanwhile.
        }
    }

    /// <summary>
    /// Reads a pending command's receipts to its outcome, asking for it again by id — under
    /// <see cref="Reconnect"/> — each time the connection it is being answered on ends first. Never
    /// faults: a command lost track of is an <see cref="ScryCommandStatus.Unknown"/> outcome.
    /// </summary>
    async Task<ScryCommandOutcome> Follow(CommandRequest request, IAsyncEnumerator<CommandReceipt>? receipts, CancelSource reading)
    {
        var token = reading.Token;
        var attempt = 1;
        var failures = 0;
        var quietSince = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                Exception? failure = null;
                try
                {
                    receipts ??= receiptTransport!(request.Id, token).GetAsyncEnumerator(token);
                    while (await receipts.MoveNextAsync().ConfigureAwait(false))
                    {
                        var receipt = receipts.Current;
                        RecordReceipt(receipt);
                        failures = 0;
                        quietSince = Stopwatch.GetTimestamp();
                        if (receipt.Status != CommandStatus.Pending)
                        {
                            return Finished(request, receipt, attempt);
                        }
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    switch (Classify(exception, reattached: attempt > 1))
                    {
                        case CommandEnding.Lost:
                            return Lost(request, attempt, exception, "The server no longer holds this command's outcome. It may have run.");
                        case CommandEnding.Failed:
                            ReportCommand(request, ScryCommandActivityKind.Failed, attempt, failure: exception);
                            return ScryCommandOutcome.Final(request.Command, request.Id, ScryCommandStatus.Failed, result: null, Shown(exception));
                    }

                    failure = exception;
                }
                finally
                {
                    if (receipts is not null)
                    {
                        await receipts.DisposeAsync().ConfigureAwait(false);
                    }

                    receipts = null;
                }

                // Ended before the outcome — cut, or ended by the server to bound its lifetime — so it
                // is asked for by id, which answers the same stream while it runs and the outcome after.
                if (receiptTransport is null)
                {
                    return Lost(request, attempt, failure, "The command's connection ended before its outcome, and this client's transport cannot ask for it again.");
                }

                var delay = Reconnect.NextDelay(new(failures, Stopwatch.GetElapsedTime(quietSince), failure));
                if (delay is not { } wait)
                {
                    return Lost(request, attempt, failure, "The command's connection ended before its outcome, and the retry policy declined to ask for it again.");
                }

                failures++;
                attempt++;
                ReportCommand(request, ScryCommandActivityKind.Reattaching, attempt, failure: failure);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Lost(request, attempt, failure: null, "The client was disposed before the command's outcome arrived.");
        }
        catch (Exception exception)
        {
            // A custom transport that throws where it should have answered: still an outcome, never a
            // fault on a task a panel is awaiting.
            return Lost(request, attempt, exception, Shown(exception));
        }
        finally
        {
            reading.Dispose();
            following.TryRemove(request.Id, out _);
        }
    }

    enum CommandEnding
    {
        AskAgain,
        Lost,
        Failed
    }

    // What ended a connection a pending command was being answered on.
    static CommandEnding Classify(Exception exception, bool reattached) =>
        exception switch
        {
            // Asked for by id and not found: pruned, never held on this node, or asked of a server that
            // no longer serves commands at all.
            ScryRequestException {StatusCode: HttpStatusCode.NotFound} or NotSupportedException when reattached => CommandEnding.Lost,

            // About the moment rather than the command.
            ScryRequestException {Code: ScryErrorCode.ExecutionFailed} => CommandEnding.AskAgain,
            ScryRequestException {Code: ScryErrorCode.Unknown} unknown when (int) unknown.StatusCode >= 500 ||
                                                                             unknown.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => CommandEnding.AskAgain,
            HttpRequestException or IOException or TimeoutException or OperationCanceledException => CommandEnding.AskAgain,

            // A rejection asking again would meet again.
            _ => CommandEnding.Failed
        };

    ScryCommandOutcome Finished(CommandRequest request, CommandReceipt receipt, int attempt)
    {
        if (receipt.Status == CommandStatus.Completed)
        {
            ReportCommand(request, ScryCommandActivityKind.Completed, attempt, receipt);
            return ScryCommandOutcome.Final(request.Command, request.Id, ScryCommandStatus.Completed, receipt.Result, error: null);
        }

        ReportCommand(request, ScryCommandActivityKind.Failed, attempt, receipt);
        return ScryCommandOutcome.Final(request.Command, request.Id, ScryCommandStatus.Failed, result: null, receipt.Error);
    }

    ScryCommandOutcome Lost(CommandRequest request, int attempt, Exception? failure, string why)
    {
        ReportCommand(request, ScryCommandActivityKind.Unknown, attempt, failure: failure);
        return ScryCommandOutcome.Final(request.Command, request.Id, ScryCommandStatus.Unknown, result: null, why);
    }

    // What a person reads: the server's message, not the envelope it came in.
    static string Shown(Exception exception) =>
        exception switch
        {
            ScryRequestException request => ScryJson.TryDeserializeError(request.Body)?.Error ?? request.Message,
            _ => exception.Message
        };

    void RecordReceipt(CommandReceipt receipt)
    {
        if (receipt.Stamp is { } stamp)
        {
            RecordServerStamp(stamp);
        }
    }

    void ReportCommand(
        CommandRequest request,
        ScryCommandActivityKind kind,
        int attempt,
        CommandReceipt? receipt = null,
        Exception? failure = null)
    {
        if (CommandActivity is not { } handler)
        {
            return;
        }

        try
        {
            handler(
                new()
                {
                    Id = request.Id,
                    Command = request.Command,
                    Kind = kind,
                    Request = request,
                    Receipt = receipt,
                    Failure = failure,
                    Attempt = attempt
                });
        }
        catch
        {
            // A diagnostic that cannot be delivered is no reason to lose a command's outcome.
        }
    }

    /// <summary>
    /// Stops following every pending command, each of which ends <see cref="ScryCommandStatus.Unknown"/>.
    /// No command is stopped: the server finishes what it accepted whether anyone is still asking.
    /// </summary>
    public void Dispose() =>
        disposing.Cancel();

    /// <summary>As <see cref="Dispose"/>, and waits for every pending command's outcome to be settled.</summary>
    public async ValueTask DisposeAsync()
    {
        disposing.Cancel();
        await Task.WhenAll(following.Values).ConfigureAwait(false);
    }

    IAsyncEnumerable<CommandReceipt> PostCommandAsync(HttpClient http, string endpoint, CommandRequest request, Cancel cancel) =>
        ReceiptsAsync(http, HttpMethod.Post, endpoint, ScryJson.SerializeToUtf8(request), bareRefusalIsNotServing: true, cancel);

    IAsyncEnumerable<CommandReceipt> GetReceiptAsync(HttpClient http, string endpoint, Guid id, Cancel cancel) =>
        ReceiptsAsync(http, HttpMethod.Get, $"{endpoint}/{id:D}", body: null, bareRefusalIsNotServing: false, cancel);

    /// <summary>
    /// Reads a command's answer: one receipt where the server decided it within its sync window, and
    /// otherwise its receipts as server-sent events, framed as a live query's answers are — ended by
    /// the outcome, or by an <c>end</c> the server sent to bound the stream's life, or cut.
    /// </summary>
    async IAsyncEnumerable<CommandReceipt> ReceiptsAsync(
        HttpClient http,
        HttpMethod method,
        string endpoint,
        byte[]? body,
        bool bareRefusalIsNotServing,
        [EnumeratorCancellation] Cancel cancel)
    {
        using var message = new HttpRequestMessage(method, endpoint);
        if (body is not null)
        {
            message.Content = JsonBody(body);
        }

        message.Headers.Accept.Add(new("application/json"));
        message.Headers.Accept.Add(new(ScryLive.ContentType));
        message.Options.Set(streamingResponse, true);

        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel);
        RecordServerHeaders(response);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsByteArrayAsync(cancel);

            // No such route, and no word from the endpoint: the server has not said how many commands
            // it will hold, so it maps none. A 404 with the endpoint's own body is the target, not the route.
            if (bareRefusalIsNotServing &&
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed &&
                ScryJson.TryDeserializeError(error) is not {Error.Length: > 0})
            {
                throw CommandsNotEnabled();
            }

            throw ResponseFailure.Read(response.StatusCode, error);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == "application/json")
        {
            yield return ScryJson.DeserializeReceipt(await ResponseBody.ReadAsync(response.Content, cancel));
            yield break;
        }

        // A success that is neither is somebody else's answer — a single-page app's fallback route
        // serving its index page for a path the server does not map, usually.
        if (mediaType != ScryLive.ContentType)
        {
            throw CommandsNotEnabled();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        var events = SseParser.Create(stream, (_, data) => data.ToArray());
        await foreach (var item in events.EnumerateAsync(cancel))
        {
            switch (item.EventType)
            {
                case ScryLive.Result:
                    yield return ScryJson.DeserializeReceipt(item.Data);
                    break;

                case ScryLive.Error:
                    throw ResponseFailure.Read(item.Data);

                case ScryLive.End:
                    yield break;
            }

            // A heartbeat, or an event from a server newer than this client.
        }
    }

    async Task<CommandCapabilities?> GetCapabilitiesAsync(HttpClient http, string endpoint, Cancel cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.Accept.Add(new("application/json"));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel);
        RecordServerHeaders(response);

        // No route: commands are off, and there is nothing this caller may send.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        var body = await ResponseBody.ReadAsync(response.Content, cancel);
        if (!response.IsSuccessStatusCode)
        {
            throw ResponseFailure.Read(response.StatusCode, body);
        }

        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            return null;
        }

        return ScryJson.DeserializeCapabilities(body);
    }

    static NotSupportedException CommandsNotEnabled() =>
        new(
            """
            This server is not serving commands.
            Set ScryOptions.MaxPendingCommands to how many it may have in flight at once, which is what maps the routes.
            """);
}
