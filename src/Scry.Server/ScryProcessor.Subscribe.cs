namespace Scry;

public sealed partial class ScryProcessor
{
    /// <summary>Subscribes without a service provider (no DI-resolved policies).</summary>
    public IAsyncEnumerable<QueryResponse> Subscribe(
        QueryRequest request,
        DbContext data,
        Cancel cancel = default) =>
        Subscribe(request, data, EmptyServiceProvider.Instance, cancel);

    /// <inheritdoc cref="Subscribe(QueryRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary, string?, Cancel)"/>
    public IAsyncEnumerable<QueryResponse> Subscribe(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        Cancel cancel = default) =>
        Subscribe(request, data, services, new HeaderDictionary(), new HeaderDictionary(), caller: null, cancel);

    /// <summary>
    /// Answers a request, and then answers it again each time the answer changes, until
    /// <paramref name="cancel"/> is cancelled or the enumeration is abandoned. The programmatic form of
    /// a live query, for a transport other than the HTTP endpoint — a hub method or a server-streaming
    /// call can return this as it is.
    /// </summary>
    /// <param name="request">The query. Any terminal is allowed: a count or a single row is as live as a list.</param>
    /// <param name="data">
    /// The context every run reads through. It is held for as long as the subscription lasts, and its
    /// change tracker is cleared before each run after the first, so it should be one the caller is
    /// not also writing through.
    /// </param>
    /// <param name="services">What policies and auditors are resolved from, on every run.</param>
    /// <param name="requestHeaders">Exposed to row policies on every run.</param>
    /// <param name="responseHeaders">
    /// What row policies may write to during the first run. A transport's headers are sent with the
    /// first answer, so later runs are given a dictionary of their own and what they write goes nowhere.
    /// </param>
    /// <param name="caller">
    /// Who this is counted against for <see cref="ScryOptions.MaxSubscriptionsPerCaller"/>, or null to
    /// count it against the server's limit only. The authenticated identity, never something the
    /// caller supplied.
    /// </param>
    /// <param name="cancel">Ends the subscription.</param>
    /// <remarks>
    /// <para>
    /// Every answer is the query run again through everything a query asked once goes through —
    /// validation, the allow-list, the row policies, the auditors — and is sent only where it differs
    /// from the one before it. So a change the caller is not allowed to see produces no answer, and
    /// when an answer arrives says nothing that asking again would not have.
    /// </para>
    /// <para>
    /// The first answer is made before this yields anything, so a request that is rejected or denied
    /// throws from the first <c>MoveNextAsync</c> exactly as <c>Execute</c> would have, and a transport
    /// can still answer it with a status. A run that fails later throws from the <c>MoveNextAsync</c>
    /// it happened under and ends the subscription; asking again is the caller's to do.
    /// </para>
    /// <para>
    /// The limits are enforced here rather than at the endpoint, so every transport has them:
    /// <see cref="ScrySubscriptionLimitException"/> is thrown, again from the first
    /// <c>MoveNextAsync</c>, where the server or the caller already holds as many as allowed.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<QueryResponse> Subscribe(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        string? caller = null,
        [EnumeratorCancellation] Cancel cancel = default)
    {
        await foreach (var frame in SubscribeBuffered(request, data, services, requestHeaders, responseHeaders, caller, cancel))
        {
            // Copied: the frame's bytes are the run's pooled buffer, and the response holds on to
            // the payload's slice of whatever it was read from.
            yield return ScryJson.DeserializeResponse(frame.Json.ToArray());
        }
    }

    /// <summary>
    /// <see cref="Subscribe(QueryRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary, string?, Cancel)"/>
    /// with each answer left as the bytes it was written as, which is what the HTTP endpoint sends.
    /// </summary>
    internal async IAsyncEnumerable<SubscriptionFrame> SubscribeBuffered(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        string? caller,
        [EnumeratorCancellation] Cancel cancel)
    {
        using var lease = subscriptions.Enter(caller, services);
        var headers = responseHeaders;
        string? last = null;
        var first = true;
        while (true)
        {
            using var output = new BoundedBufferWriter(options.MaxSubscriptionBytes);
            var json = await Run(lease, request, data, services, requestHeaders, headers, output, first, cancel);
            var id = Fingerprint.Of(json.Span);

            // Compared before it is sent, which is what keeps the timing of an answer from saying
            // anything: a write the caller may not see changes nothing here, so nothing goes out.
            if (id != last)
            {
                last = id;
                yield return new(id, json);
            }

            // The transport's headers left with the first answer. ApplySensitivity alone assigns one
            // on every run, and a committed dictionary refuses the write.
            first = false;
            headers = new HeaderDictionary();

            await lease.WaitUntilDue(cancel);
        }
    }

    async ValueTask<ReadOnlyMemory<byte>> Run(
        SubscriptionLease lease,
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BoundedBufferWriter output,
        bool first,
        Cancel cancel)
    {
        // Given back before the answer is handed on: a reader that is slow to take it holds its own
        // connection, never one of the places at the database.
        using var slot = await subscriptions.RunSlot(cancel);
        lease.BeginRun();

        // Before rather than after, so that a policy reading through the context finds what is in the
        // database now rather than what the run before left in the identity map.
        if (!first)
        {
            data.ChangeTracker.Clear();
        }

        var run = new SubscriptionRun();
        var fallback = await TryExecuteBufferedAsync(
            request,
            data,
            services,
            requestHeaders,
            responseHeaders,
            output,
            cancel: cancel,
            subscription: run);
        lease.EndRun(run.Dependencies);

        // The rare envelope a drifted client is answered with, which the row writer does not produce.
        if (fallback is not null)
        {
            return ScryJson.SerializeToUtf8(fallback);
        }

        return output.WrittenMemory;
    }
}
