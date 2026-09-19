namespace Scry;

/// <summary>
/// A live query being delivered to a callback, from <see cref="ScryLiveQuery{TResult}.Subscribe(Action{TResult}, Action{Exception}?)"/>.
/// Dispose it to stop: the server is told, and nothing is delivered afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Answers are delivered one at a time, in order, and the next is not read until the callback for the
/// last has returned — so a callback that is slow is never handed a backlog, only the latest state
/// when it is ready for one.
/// </para>
/// <para>
/// A subscription made where there is a <see cref="SynchronizationContext"/> — a WPF or Windows Forms
/// UI thread, a Blazor Server circuit — delivers on it, so a callback may touch what that context
/// owns without marshalling. The reading and deserializing happen off it.
/// </para>
/// </remarks>
public sealed class ScrySubscription :
    IAsyncDisposable,
    IDisposable
{
    CancelSource ending = new();
    SynchronizationContext? context;
    Lock gate = new();
    bool stopped;

    ScrySubscription(SynchronizationContext? context) =>
        this.context = context;

    /// <summary>Where this subscription is in its life.</summary>
    public ScrySubscriptionState State { get; private set; } = ScrySubscriptionState.Connecting;

    /// <summary>
    /// What ended the subscription, once <see cref="State"/> is <see cref="ScrySubscriptionState.Faulted"/>:
    /// a failure asking again would not fix, or one thrown by the callback itself.
    /// </summary>
    public Exception? Error { get; private set; }

    /// <summary>Raised when <see cref="State"/> changes, where answers are delivered.</summary>
    public event Action<ScrySubscriptionState>? StateChanged;

    /// <summary>
    /// Completes when the subscription is over, however it ended. It never faults — a failure is on
    /// <see cref="Error"/> and was handed to the error callback — so it is safe to leave unobserved.
    /// </summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    internal static ScrySubscription Start<TResult>(
        Func<Action<ScrySubscriptionState>, Cancel, IAsyncEnumerable<TResult>> open,
        Func<TResult, Task> onNext,
        Action<Exception>? onError,
        Action? onCompleted,
        bool captureContext)
    {
        var subscription = new ScrySubscription(captureContext ? SynchronizationContext.Current : null);

        // On the pool rather than where Subscribe was called: reading and deserializing an answer is
        // work a UI thread should not be doing. Only the callbacks go back.
        subscription.Completion = Task.Run(() => subscription.Pump(open, onNext, onError, onCompleted));
        return subscription;
    }

    async Task Pump<TResult>(
        Func<Action<ScrySubscriptionState>, Cancel, IAsyncEnumerable<TResult>> open,
        Func<TResult, Task> onNext,
        Action<Exception>? onError,
        Action? onCompleted)
    {
        try
        {
            await foreach (var answer in open(Report, ending.Token).WithCancellation(ending.Token).ConfigureAwait(false))
            {
                await Deliver(() => onNext(answer)).ConfigureAwait(false);
            }

            await Finish(ScrySubscriptionState.Closed, onCompleted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            // Disposed. Nothing is delivered after that, the closing state included.
            State = ScrySubscriptionState.Closed;
        }
        catch (Exception exception)
        {
            Error = exception;
            await Finish(ScrySubscriptionState.Faulted, onError is null ? null : () => onError(exception)).ConfigureAwait(false);
        }
    }

    // The pump reports from wherever it is running, and a state change is delivered like an answer.
    void Report(ScrySubscriptionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        _ = Quietly(state);
    }

    // Not awaited by the pump, which has an answer to get on with — so a handler that throws is
    // caught here rather than left on a task nobody looks at.
    async Task Quietly(ScrySubscriptionState state)
    {
        try
        {
            await Deliver(
                () =>
                {
                    StateChanged?.Invoke(state);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A state change is a courtesy. The answers are what a failure is reported for.
        }
    }

    async Task Finish(ScrySubscriptionState state, Action? last)
    {
        State = state;
        try
        {
            await Deliver(
                () =>
                {
                    StateChanged?.Invoke(state);
                    last?.Invoke();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The subscription is already over; a callback that throws while being told so has
            // nowhere left to be reported.
        }
    }

    /// <summary>
    /// Runs a callback where answers are delivered, and completes when it has. Held under the same
    /// gate <see cref="Dispose"/> takes, which is what makes "nothing is delivered after Dispose
    /// returns" true rather than likely.
    /// </summary>
    Task Deliver(Func<Task> callback)
    {
        if (context is null)
        {
            return Guarded(callback);
        }

        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(
            async _ =>
            {
                try
                {
                    await Guarded(callback);
                    delivered.SetResult();
                }
                catch (Exception exception)
                {
                    delivered.SetException(exception);
                }
            },
            null);
        return delivered.Task;
    }

    Task Guarded(Func<Task> callback)
    {
        lock (gate)
        {
            if (stopped)
            {
                return Task.CompletedTask;
            }

            // Started under the gate, so a Dispose racing it waits for the synchronous part — all of
            // an ordinary callback. What an asynchronous one does after its first await is its own.
            return callback();
        }
    }

    /// <summary>Stops the subscription. Nothing is delivered once this has returned.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            stopped = true;
        }

        ending.Cancel();
    }

    /// <summary>Stops the subscription and waits for its connection to be given up.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }
}
