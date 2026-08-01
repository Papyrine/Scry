namespace Scry;

/// <summary>
/// The element of the collection a <see cref="SubqueryNode"/> is reading, used where a
/// <see cref="MemberNode"/> would be over a collection of rows. A collection of <i>values</i> — an EF
/// primitive collection, typically a JSON column — has no member to name, so this is how its element
/// is read: <c>Tags.Any(_ =&gt; _ == "urgent")</c> is a binary node over this and a constant, and
/// <c>Scores.Sum()</c> is a sum whose selector is this.
/// </summary>
/// <remarks>
/// Only meaningful where the row being read is a value rather than an allow-listed type, which is
/// exactly inside a subquery over a collection of scalars. Anywhere else the server rejects it: it
/// would otherwise name a whole entity, which is not something a query may compare, order by, or
/// project.
/// </remarks>
public sealed record ElementNode :
    Node;
