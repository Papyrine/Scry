/// <summary>
/// Ambient context for <see cref="TolerantEnumConverterFactory"/>: non-null exactly while a response
/// payload is being deserialized (see <c>ScryJson.DeserializePayload</c>), carrying the response's
/// enum aliases. AsyncLocal so concurrent deserializations cannot see each other's aliases. Outside a
/// payload — request parsing server-side, the response envelope itself — it stays null and the
/// converter fails closed exactly as before.
/// </summary>
static class EnumAliasScope
{
    static readonly AsyncLocal<IReadOnlyList<EnumAlias>?> current = new();

    public static IReadOnlyList<EnumAlias>? Current
    {
        get => current.Value;
        set => current.Value = value;
    }
}
