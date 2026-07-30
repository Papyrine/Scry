namespace Scry.Wire;

/// <summary>
/// Reads a string value under a particular case sensitivity, so the comparisons wrapping it are made
/// that way. Composes rather than doubling the function set: <c>Contains(Collate(Name, …), term)</c>.
/// </summary>
/// <remarks>
/// The node carries a <see cref="StringMatch"/>, not a collation name. A collation cannot be a query
/// parameter — it is emitted into the SQL text — so the string that implements each intent comes from
/// server configuration and never from a request. A server that has configured none rejects the node.
/// </remarks>
public sealed record CollateNode(Node Target, StringMatch Match) :
    Node;
