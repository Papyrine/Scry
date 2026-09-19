namespace Scry;

/// <summary>How the Redis backplane is set up.</summary>
public sealed class ScryRedisOptions
{
    /// <summary>
    /// The pub/sub channel changes are published on. Default <c>scry:changes</c>. Every node of one
    /// deployment uses the same one, and two deployments sharing a Redis use two.
    /// </summary>
    /// <remarks>
    /// A change names entities by their EF entity type name, which says nothing about which database
    /// they are in. Two apps on one channel would each re-ask their live queries for the other's
    /// writes — harmless, since every answer is still the query run again, and wasted.
    /// </remarks>
    public string Channel { get; set; } = "scry:changes";
}
