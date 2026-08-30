namespace Scry;

/// <summary>
/// Collects several queries and sends them as one request. A query is added by writing it exactly as
/// it would be written on its own and attaching the batch with
/// <see cref="ScryBatchExtensions.InBatch{T}"/>; every terminal works unchanged, and the task it
/// returns completes when <see cref="SendAsync"/> does.
/// </summary>
/// <remarks>
/// <para>
/// The terminal's task does not complete until the batch is sent, so <b>awaiting it before
/// <see cref="SendAsync"/> waits forever</b>. Collect the tasks, send, then await them.
/// </para>
/// <para>
/// A batch is a client-side collector used once, from one thread — the page load it belongs to. It
/// saves round-trips, not database time: the server runs the entries sequentially, and they are
/// independent rather than transactional.
/// </para>
/// </remarks>
public sealed class ScryBatch
{
    readonly ScryClient client;
    readonly List<Entry> entries = [];
    bool sent;

    internal ScryBatch(ScryClient client) =>
        this.client = client;

    readonly record struct Entry(QueryRequest Request, TaskCompletionSource<QueryResponse> Completion);

    /// <summary>The number of queries collected so far.</summary>
    public int Count => entries.Count;

    /// <summary>True once <see cref="SendAsync"/> has been called.</summary>
    public bool Sent => sent;

    /// <summary>
    /// Checks what a query may not do inside a batch. Called by <c>InBatch</c> so the failure lands on
    /// the line that attached the batch, and again on enqueue to catch a query that acquired headers
    /// after being attached.
    /// </summary>
    internal void Attaching(ScryCall? call)
    {
        if (sent)
        {
            throw new InvalidOperationException(
                "This batch has already been sent. Create another with ScryClient.Batch() for further queries.");
        }

        // A batch is one request carrying many queries, so a per-query header has no request of its own
        // to be written onto. Refused rather than dropped, exactly as a custom transport refuses them.
        if (call is null)
        {
            return;
        }

        throw new NotSupportedException("Per-query headers cannot be used inside a batch: the batch is a single request, so its queries cannot carry headers of their own. Send the query on its own, or set the header on the HttpClient.");
    }

    internal Task<QueryResponse> Enqueue(QueryRequest request, ScryCall? call)
    {
        Attaching(call);

        // Continuations run asynchronously so that awaiting a query's task cannot resume user code
        // inside SendAsync's completion loop and stall the entries after it.
        TaskCompletionSource<QueryResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        entries.Add(new(request, completion));
        return completion.Task;
    }

    /// <summary>
    /// Sends every collected query as one request and completes each one's task with its own result.
    /// An entry the server rejected faults its own task with the exception it would have thrown had the
    /// query been sent alone, leaving the other entries answered.
    /// </summary>
    /// <remarks>
    /// Only a failure of the batch itself — the transport, an unreadable response, or a rejection of
    /// the whole envelope — throws from here. Such a failure also faults every entry's task, so a
    /// caller awaiting them is never left waiting on a batch that will never arrive.
    /// </remarks>
    public async Task SendAsync(Cancel cancel = default)
    {
        if (sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        sent = true;
        if (entries.Count == 0)
        {
            return;
        }

        QueryBatchResponse response;
        try
        {
            response = await client.SendBatchAsync(
                QueryBatchRequest.Create([..entries.Select(_ => _.Request)]),
                cancel);
        }
        catch (Exception exception)
        {
            Fault(exception);
            throw;
        }

        var results = response.Results;
        if (results.Count != entries.Count)
        {
            var mismatch = new ScryWireException(
                $"The server answered {results.Count} of the batch's {entries.Count} queries.");
            Fault(mismatch);
            throw mismatch;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            Complete(entries[i].Completion, results[i], response.BinaryParts);
        }
    }

    static void Complete(TaskCompletionSource<QueryResponse> completion, QueryBatchResult result, IReadOnlyList<byte[]>? parts)
    {
        if (result.Response is { } response)
        {
            // A batch's parts are numbered globally, so every entry resolves against the whole list.
            completion.SetResult(response with {BinaryParts = parts});
            return;
        }

        var error = result.Error ?? "The server returned neither a result nor an error for this query.";

        // The same exceptions the single-query path raises, so code that handles a failed query does
        // not have to learn a second shape for one that happened to be batched.
        completion.SetException(
            GetException(result, error));
    }

    static Exception GetException(QueryBatchResult result, string error)
    {
        if (result.StaleClient)
        {
            return new ScryStaleClientException(error);
        }

        if (result.Status == HttpStatusCode.Forbidden)
        {
            return new ScryPermissionException(error);
        }

        return new ScryRequestException(result.Status, error);
    }

    void Fault(Exception exception)
    {
        foreach (var (_, completion) in entries)
        {
            completion.TrySetException(exception);
        }
    }
}
