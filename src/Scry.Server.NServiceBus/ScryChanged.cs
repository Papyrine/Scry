namespace Scry;

/// <summary>
/// The event that says data changed: published by whichever endpoint wrote it, handled by every Scry
/// server, which re-asks the live queries that read what it names.
/// </summary>
/// <remarks>
/// <para>
/// It carries what <see cref="ScryChange.Serialize"/> writes — entity names and the node that raised
/// it, never a row — so whoever can publish one can cause live queries to be asked again and nothing
/// else: every answer still comes from running the query through its policies.
/// </para>
/// <para>
/// Short-lived on purpose. A change that arrives a minute late is one the live query's own poll has
/// long since caught, and a queue of them left behind by a server that was down is work for nothing
/// when it comes back.
/// </para>
/// </remarks>
[TimeToBeReceived("00:01:00")]
public sealed class ScryChanged :
    IEvent
{
    /// <summary>The change, as <see cref="ScryChange.Serialize"/> wrote it.</summary>
    public string Payload { get; set; } = "";
}
