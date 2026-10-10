namespace Scry;

/// <summary>
/// One thing an answer carried, by position: the address of a row, a scalar, a receipt.
/// </summary>
/// <param name="Ordinal">Where it sat in the answer, counted from zero.</param>
/// <param name="Content">Where what it carried is kept.</param>
public readonly record struct ScryDisclosureUnit(int Ordinal, ScryDisclosureAddress Content);

/// <summary>
/// Which row of which source a unit was read from. One per row a unit carries anything of: the root
/// row, and each row reached through a navigation or a join.
/// </summary>
/// <param name="Ordinal">The unit it belongs to.</param>
/// <param name="Slot">Which of the unit's rows this is, counted from zero. The root row is first.</param>
/// <param name="Source">The source the row belongs to, by the wire name of the top of its hierarchy.</param>
/// <param name="Key">
/// The row's primary key as a JSON array of its values in the key's own order: <c>[5]</c>,
/// <c>["A",7]</c>.
/// </param>
/// <param name="Via">
/// How the unit reached the row: empty for the root, a navigation path, <c>inner</c> for a join's
/// other side, <c>target</c> for the row a command was sent against.
/// </param>
public readonly record struct ScryDisclosureEntity(int Ordinal, int Slot, string Source, string Key, string Via)
{
    /// <summary>
    /// A key as <see cref="Key"/> holds it: the JSON array of its values, each written as a response
    /// writes a value of that type. What a store asked about a row turns the key it was given into,
    /// so the question and the record meet on the same text.
    /// </summary>
    /// <param name="values">
    /// The primary-key values in the key's own order, each of the key member's own type: an
    /// <see cref="int"/> key is asked about with an <see cref="int"/>, a <see cref="Guid"/> with a
    /// <see cref="Guid"/>.
    /// </param>
    public static string KeyOf(IReadOnlyList<object?> values)
    {
        using var buffer = new PooledBufferWriter();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var value in values)
            {
                PlanShapeWriter.WriteValue(json, value);
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenMemory.Span);
    }
}

/// <summary>
/// A piece of content and its address. <see cref="Bytes"/> is empty where the content is recorded by
/// its digest alone — a binary value, unless <see cref="ScryDisclosureOptions.StoreBinaryContent"/>
/// says to keep it.
/// </summary>
/// <param name="Address">Where it is kept.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Length">How many bytes it is, whether or not they are held here.</param>
/// <param name="Bytes">
/// The content in canonical form. Valid only until the sink it was handed to returns: a sink that
/// keeps content copies it.
/// </param>
public readonly record struct ScryDisclosureContent(
    ScryDisclosureAddress Address,
    ScryDisclosureContentKind Kind,
    int Length,
    ReadOnlyMemory<byte> Bytes)
{
    /// <summary>Whether the bytes are held, rather than only their digest and length.</summary>
    public bool Held => Bytes.Length == Length;
}

/// <summary>What a piece of content is.</summary>
public enum ScryDisclosureContentKind : byte
{
    /// <summary>One projected row, as the JSON object the wire carried.</summary>
    Row = 1,

    /// <summary>A scalar answer, as its JSON value.</summary>
    Scalar = 2,

    /// <summary>A request, as canonical JSON.</summary>
    Request = 3,

    /// <summary>A shape descriptor.</summary>
    Shape = 4,

    /// <summary>A command receipt.</summary>
    Receipt = 5,

    /// <summary>A caller's capabilities.</summary>
    Capabilities = 6,

    /// <summary>The introspection document.</summary>
    Schema = 7,

    /// <summary>SQL text.</summary>
    Sql = 8,

    /// <summary>Binary: an attachment, or a <c>byte[]</c> member's value.</summary>
    Bytes = 9,

    /// <summary>What a reviewer asked of the store.</summary>
    Parameters = 10
}

/// <summary>
/// What an answer's rows were made of: every member of every source the query read, and what it did
/// with each. Addressed like content, so one shape is stored once for every answer of that query.
/// </summary>
/// <param name="Address">Where the shape is kept.</param>
/// <param name="Fields">The members read, each with its use.</param>
public sealed record ScryDisclosureShape(ScryDisclosureAddress Address, IReadOnlyList<ScryDisclosureField> Fields);

/// <summary>One member a query read, and what it did with it.</summary>
/// <param name="Source">The type the member belongs to, by wire name.</param>
/// <param name="Member">The member.</param>
/// <param name="Use">What the query did with it.</param>
/// <param name="Sensitive">Whether the model marks it <c>[Sensitive]</c>.</param>
public readonly record struct ScryDisclosureField(string Source, string Member, ScryDisclosureFieldUse Use, bool Sensitive);

/// <summary>What a query did with a member it read.</summary>
public enum ScryDisclosureFieldUse : byte
{
    /// <summary>Its value left in a row.</summary>
    Returned = 1,

    /// <summary>A value folded from it left: a sum, a minimum, a joined string.</summary>
    Aggregated = 2,

    /// <summary>
    /// It decided which rows left or in what order: a filter, an ordering, a grouping or a join key.
    /// Nothing of it was returned, but an answer shaped by it says something about it.
    /// </summary>
    Read = 3,

    /// <summary>It was stepped through to reach another row.</summary>
    Traversed = 4,

    /// <summary>It is a <c>Can{Command}</c> capability, computed for the row and returned.</summary>
    Capability = 5
}
