namespace Scry;

/// <summary>How the MessagePipe backplane is set up.</summary>
public sealed class ScryMessagePipeOptions
{
    /// <summary>
    /// The key changes are published under. Default <c>scry:changes</c>. Every node of one deployment
    /// uses the same one, and two deployments sharing a transport use two.
    /// </summary>
    public string Topic { get; set; } = "scry:changes";
}
