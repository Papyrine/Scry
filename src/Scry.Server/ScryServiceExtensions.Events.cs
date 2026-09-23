using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;

namespace Scry;

// The server-sent-events framing every held response shares — a live query's answers and a pending
// command's receipts alike: the headers that keep a response from being buffered, one flushed event at
// a time, a heartbeat on a fixed clock, and an end at the connection's lifetime or the ticket's expiry.
public static partial class ScryServiceExtensions
{
    static void Commit(HttpContext context)
    {
        var response = context.Response;
        response.ContentType = ScryLive.ContentType;

        // Rows shaped by who asked, on a response that never ends: nothing between here and the
        // caller has any business keeping it, or holding it back to send in larger pieces. The last
        // two say that to the places known to do so — a reverse proxy, and compression middleware.
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        response.Headers.ContentEncoding = "identity";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    /// <summary>
    /// Holds a committed response open: hands on each item as <paramref name="pending"/> completes, and
    /// sends a heartbeat when none has arrived for a while, until the client goes,
    /// <paramref name="deliver"/> says to stop, or the server has a reason of its own to end it.
    /// </summary>
    /// <param name="context">The request being answered. Its abort, or the expiry of the ticket that authenticated it, ends the hold.</param>
    /// <param name="options">Supplies the heartbeat interval, and the lifetime after which the hold ends.</param>
    /// <param name="pending">The item being waited for: true once one is ready to be delivered.</param>
    /// <param name="deliver">Writes the item that arrived. False to stop holding.</param>
    static async Task Hold(HttpContext context, ScryOptions options, Func<Task<bool>> pending, Func<Task<bool>> deliver)
    {
        var stopping = context.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? Cancel.None;
        var (deadline, reason) = Deadline(context, options);
        var nextPing = DateTimeOffset.UtcNow + options.SubscriptionHeartbeat;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var wake = deadline is { } at && at < nextPing ? at : nextPing;
            if (await Arrives(pending(), wake, context.RequestAborted, stopping))
            {
                if (!await deliver())
                {
                    return;
                }

                // An item says the connection is alive as well as a heartbeat does.
                nextPing = DateTimeOffset.UtcNow + options.SubscriptionHeartbeat;
                continue;
            }

            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            if (stopping.IsCancellationRequested)
            {
                await End(context, "shutdown");
                return;
            }

            if (deadline is { } due &&
                DateTimeOffset.UtcNow >= due)
            {
                await End(context, reason);
                return;
            }

            await WriteEvent(context, ScryLive.Ping, id: null, data: default);

            // Kept to the clock rather than measured from this write. Work that found nothing to say
            // happens on its own task and never delays this one, so a heartbeat is never late for a
            // reason a caller could read a write into.
            nextPing += options.SubscriptionHeartbeat;
            if (nextPing <= DateTimeOffset.UtcNow)
            {
                nextPing = DateTimeOffset.UtcNow + options.SubscriptionHeartbeat;
            }
        }
    }

    // Whether the next item arrived before it was time to do something else.
    static async Task<bool> Arrives(Task<bool> pending, DateTimeOffset wake, Cancel aborted, Cancel stopping)
    {
        var wait = wake - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            return pending.IsCompleted;
        }

        // Linked so that an item arriving first releases the timer rather than leaving it to run out.
        using var waking = CancelSource.CreateLinkedTokenSource(aborted, stopping);
        var first = await Task.WhenAny(pending, Task.Delay(wait, waking.Token));
        await waking.CancelAsync();
        return first == pending;
    }

    static Task Fail(HttpContext context, string message, ScryErrorCode code) =>
        WriteEvent(
            context,
            ScryLive.Error,
            id: null,
            ScryJson.SerializeToUtf8(
                new ScryError(message)
                {
                    Code = code
                }));

    // Ended to be asked again. Reconnecting is a new request, authenticated and authorized as one —
    // which is the whole reason a held response is not allowed to last for ever.
    static Task End(HttpContext context, string reason) =>
        WriteEvent(
            context,
            ScryLive.End,
            id: null,
            ScryJson.SerializeToUtf8(
                new ScryLiveEnd(true)
                {
                    Reason = reason
                }));

    /// <summary>
    /// When this connection has to end whatever happens on it: the configured lifetime, or the moment
    /// the ticket that authenticated it stops being valid, whichever comes first.
    /// </summary>
    /// <remarks>
    /// Read off the feature the authentication middleware leaves rather than by authenticating again:
    /// a host with no default scheme has nothing to authenticate with, and would throw.
    /// </remarks>
    static (DateTimeOffset? At, string Reason) Deadline(HttpContext context, ScryOptions options)
    {
        var lifetime = options.SubscriptionLifetime is { } span
            ? DateTimeOffset.UtcNow + span
            : (DateTimeOffset?)null;
        var ticket = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        if (ticket is { } expires &&
            (lifetime is null || expires < lifetime))
        {
            return (expires, "expired");
        }

        return (lifetime, "lifetime");
    }

    /// <summary>
    /// Writes one server-sent event and flushes it. Written by hand rather than through the
    /// framework's formatter, which never flushes of its own accord and has no way to be handed bytes
    /// that are only valid until the next item.
    /// </summary>
    /// <remarks>
    /// Every event carries a <c>data:</c> line, including the two with nothing to say: the platform's
    /// parser does not dispatch an event without one. The data is one line by construction — it is
    /// JSON from a writer that does not indent, and JSON escapes a line break inside a string.
    /// </remarks>
    static async Task WriteEvent(HttpContext context, string name, string? id, ReadOnlyMemory<byte> data)
    {
        Debug.Assert(data.Span.IndexOf((byte)'\n') < 0, "A server-sent event's data must be one line.");

        var cancel = context.RequestAborted;
        var body = context.Response.Body;
        var head = id is null ? $"event: {name}\ndata: " : $"event: {name}\nid: {id}\ndata: ";
        await body.WriteAsync(Encoding.UTF8.GetBytes(head), cancel);
        if (!data.IsEmpty)
        {
            await body.WriteAsync(data, cancel);
        }

        await body.WriteAsync(eventEnd, cancel);
        await body.FlushAsync(cancel);
    }

    static byte[] eventEnd = "\n\n"u8.ToArray();

    const string retryAfterSeconds = "5";

    /// <summary>
    /// A held response's items, with the one being waited for kept in hand. An async iterator refuses
    /// to be disposed while it is being asked for its next value, so ending one means cancelling it,
    /// letting that request finish, and only then disposing — in that order, every time, which is the
    /// reason this exists rather than three lines at each place a stream can end.
    /// </summary>
    sealed class HeldItems<T>(IAsyncEnumerable<T> source, CancelSource ending) :
        IAsyncDisposable
    {
        IAsyncEnumerator<T> items = source.GetAsyncEnumerator(ending.Token);
        Task<bool>? pending;

        /// <summary>The item being waited for. Asked for on first use, and again after each <see cref="Advance"/>.</summary>
        public Task<bool> Pending => pending ??= items.MoveNextAsync().AsTask();

        public T Current => items.Current;

        public void Advance() =>
            pending = null;

        public async ValueTask DisposeAsync()
        {
            await ending.CancelAsync();
            if (pending is not null)
            {
                try
                {
                    await pending;
                }
                catch (Exception)
                {
                    // Already answered for, or the cancellation just asked for.
                }
            }

            await items.DisposeAsync();
        }
    }
}
