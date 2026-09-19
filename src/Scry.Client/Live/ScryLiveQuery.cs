namespace Scry;

/// <summary>
/// A query that is answered now and again whenever its answer changes. Made by <c>Live()</c> and the
/// terminals beside it, and consumed whichever way suits: enumerated as a stream, handed a callback,
/// or taken as an <see cref="IObservable{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// It is a description, not a connection: nothing is asked of the server until it is enumerated or
/// subscribed to, and each time it is, that consumer gets a server subscription of its own. Two
/// components that want the same answers from one subscription share the consumer, not this.
/// </para>
/// <para>
/// Every answer is the whole of the query's current result — a list's rows, a count's number — never
/// a difference from the one before. The values a query closes over are read once, when it was made:
/// a live query over <c>_.Region == region</c> keeps asking about the region that variable held then.
/// </para>
/// <para>
/// A connection that ends is asked for again under <see cref="ScryClient.Reconnect"/>, without the
/// consumer seeing more than a pause. What does end a live query is the consumer stopping, the server
/// saying it will not answer again, or a failure asking again would not fix: a rejection, a denial, a
/// client the server calls stale.
/// </para>
/// </remarks>
public sealed class ScryLiveQuery<TResult> :
    IAsyncEnumerable<TResult>
{
    ScryClient client;
    QueryRequest request;
    ScryCall? call;
    Func<QueryResponse, TResult> read;

    internal ScryLiveQuery(ScryClient client, QueryRequest request, ScryCall? call, Func<QueryResponse, TResult> read)
    {
        this.client = client;
        this.request = request;
        this.call = call;
        this.read = read;
    }

    /// <summary>The request this live query sends — the one the same query asked once would.</summary>
    public QueryRequest Request => request;

    /// <summary>
    /// Starts a server subscription and yields its answers until <paramref name="cancel"/> is
    /// cancelled or the enumeration is abandoned. The next answer is read only when it is asked for,
    /// so a consumer that falls behind is handed the latest state rather than a backlog.
    /// </summary>
    public IAsyncEnumerator<TResult> GetAsyncEnumerator(Cancel cancel = default) =>
        Answers(changed: null, cancel).GetAsyncEnumerator(cancel);

    /// <summary>
    /// Starts a server subscription and hands each answer to <paramref name="onNext"/>, until the
    /// result is disposed.
    /// </summary>
    /// <param name="onNext">Called with each answer, one at a time and in order.</param>
    /// <param name="onError">
    /// Called once if the live query ends on a failure, including one <paramref name="onNext"/> threw.
    /// Without it a failure is still on <see cref="ScrySubscription.Error"/>.
    /// </param>
    public ScrySubscription Subscribe(Action<TResult> onNext, Action<Exception>? onError = null) =>
        ScrySubscription.Start(
            Answers,
            answer =>
            {
                onNext(answer);
                return Task.CompletedTask;
            },
            onError,
            onCompleted: null,
            captureContext: true);

    /// <summary>
    /// The same, for a callback that awaits. The next answer is not read until the task it returns
    /// has completed.
    /// </summary>
    /// <remarks>
    /// An overload of its own so that <c>async answer =&gt; …</c> binds here rather than to the
    /// <see cref="Action{T}"/> form, where it would be an <c>async void</c> whose failures go nowhere.
    /// </remarks>
    public ScrySubscription Subscribe(Func<TResult, Task> onNext, Action<Exception>? onError = null) =>
        ScrySubscription.Start(Answers, onNext, onError, onCompleted: null, captureContext: true);

    /// <summary>
    /// This live query as an <see cref="IObservable{T}"/>, for Rx or anything else built on the
    /// interface. Each observer gets a server subscription of its own, started when it subscribes and
    /// ended when what it was handed back is disposed.
    /// </summary>
    /// <remarks>
    /// Calls to an observer never overlap, at most one of <c>OnError</c> and <c>OnCompleted</c> is ever
    /// made and nothing follows it, and nothing at all is called once the subscription's
    /// <c>Dispose</c> has returned. No synchronization context is captured: an observable's consumer
    /// says where it wants to be called by composing that in.
    /// </remarks>
    public IObservable<TResult> AsObservable() =>
        new Observable(this);

    async IAsyncEnumerable<TResult> Answers(Action<ScrySubscriptionState>? changed, [EnumeratorCancellation] Cancel cancel)
    {
        await foreach (var response in LivePump.Answers(client, request, call, changed, cancel).WithCancellation(cancel))
        {
            yield return read(response);
        }
    }

    sealed class Observable(ScryLiveQuery<TResult> query) :
        IObservable<TResult>
    {
        public IDisposable Subscribe(IObserver<TResult> observer) =>
            ScrySubscription.Start(
                query.Answers,
                answer =>
                {
                    observer.OnNext(answer);
                    return Task.CompletedTask;
                },
                observer.OnError,
                observer.OnCompleted,
                captureContext: false);
    }
}
