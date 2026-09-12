/// <summary>
/// Options for the state the explorer persists into browser storage — the open tabs and the query
/// history. Deliberately not <see cref="ScryJson.Options"/>: this is local state a reader on the
/// same machine round-trips, not the wire, and it must not be tied to a contract that cannot move.
/// </summary>
static class StorageJson
{
    // camelCase to match everything else the browser holds.
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
