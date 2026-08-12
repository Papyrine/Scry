/// <summary>
/// Ambient context for <see cref="TolerantEnumConverterFactory"/>: non-null exactly while a response
/// payload is being deserialized (see <c>ScryJson.DeserializePayload</c>), carrying the response's
/// enum aliases. Outside a payload — request parsing server-side, the response envelope itself — it
/// stays null and the converter fails closed exactly as before.
/// </summary>
/// <remarks>
/// <c>[ThreadStatic]</c> rather than <c>AsyncLocal</c>: the scope is set and cleared around a wholly
/// synchronous <c>Deserialize</c> call, with no await between, so a thread cannot observe another's
/// value and the isolation an <c>AsyncLocal</c> bought is already had. What it costs differs, though —
/// every <c>AsyncLocal</c> write copies the execution context, which a per-row set and reset paid for
/// on each streamed row.
/// </remarks>
static class EnumAliasScope
{
    [ThreadStatic]
    static IReadOnlyList<EnumAlias>? current;

    public static IReadOnlyList<EnumAlias>? Current
    {
        get => current;
        set => current = value;
    }
}
