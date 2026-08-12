/// <summary>
/// Ambient context for <see cref="BinaryConverter"/>: non-null exactly while a payload or row that
/// arrived with multipart binary parts is being deserialized (see <c>ScryJson.DeserializePayload</c>
/// and <c>DeserializeRow</c>), carrying those parts in wire order. Outside such a payload it stays
/// null and a placeholder read fails closed.
/// </summary>
/// <remarks>
/// <c>[ThreadStatic]</c> for the reason <see cref="EnumAliasScope"/> is: the scope wraps a synchronous
/// deserialization, so it isolates threads just as an <c>AsyncLocal</c> did, without the execution
/// context copy each write of one costs.
/// </remarks>
static class BinaryPartScope
{
    [ThreadStatic]
    static IReadOnlyList<byte[]>? current;

    public static IReadOnlyList<byte[]>? Current
    {
        get => current;
        set => current = value;
    }
}
