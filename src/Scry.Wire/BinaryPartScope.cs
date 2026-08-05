/// <summary>
/// Ambient context for <see cref="BinaryConverter"/>: non-null exactly while a payload or row that
/// arrived with multipart binary parts is being deserialized (see <c>ScryJson.DeserializePayload</c>
/// and <c>DeserializeRow</c>), carrying those parts in wire order. AsyncLocal so concurrent
/// deserializations cannot see each other's parts. Outside such a payload it stays null and a
/// placeholder read fails closed.
/// </summary>
static class BinaryPartScope
{
    static readonly AsyncLocal<IReadOnlyList<byte[]>?> current = new();

    public static IReadOnlyList<byte[]>? Current
    {
        get => current.Value;
        set => current.Value = value;
    }
}
