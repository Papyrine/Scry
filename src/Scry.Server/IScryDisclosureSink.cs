namespace Scry;

/// <summary>
/// Where disclosure records are accepted. Set with <see cref="ScryOptions.UseDisclosureAudit(IScryDisclosureSink, Action{ScryDisclosureOptions}?)"/>;
/// the audit is off until one is.
/// </summary>
/// <remarks>
/// <para>
/// Accepting is the whole contract: a call returns only once the batch is as durable as this sink
/// makes anything. Scry hands content to a transport only after the call describing it has returned,
/// so what a caller received is never missing from what a sink accepted — and a sink that throws
/// fails the response rather than letting it go unrecorded.
/// </para>
/// <para>
/// A batch is identified by its <see cref="ScryDisclosureBatch.EventId"/> and
/// <see cref="ScryDisclosureBatch.Sequence"/>, and may be delivered more than once: a sink that
/// already holds one accepts it again and changes nothing.
/// </para>
/// <para>
/// The content a batch carries is valid only until the call returns. A sink that keeps content copies
/// it.
/// </para>
/// </remarks>
// begin-snippet: disclosureSink
public interface IScryDisclosureSink
{
    /// <summary>Accepts a batch, blocking until it is durable. What the blocking processor surface calls.</summary>
    void Append(ScryDisclosureBatch batch);

    /// <summary>Accepts a batch, completing once it is durable. What the endpoints call.</summary>
    ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel);
}
// end-snippet

/// <summary>
/// Thrown where the disclosure audit could not record an answer, which is why the answer was not
/// sent. To a client it is the fixed execution failure; the real reason is the inner exception, and
/// what an auditor is given.
/// </summary>
public sealed class ScryDisclosureException(string message, Exception? inner = null) :
    Exception(message, inner);
