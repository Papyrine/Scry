namespace Scry.Wire;

/// <summary>
/// A serialized query result. <see cref="Payload"/> is a JSON array of projected rows for
/// <see cref="ResultKind.List"/>, a single projected object for <see cref="ResultKind.Single"/>,
/// a scalar for <see cref="ResultKind.Scalar"/>, or a <see cref="ScryPage{T}"/> envelope
/// (<c>items</c> + <c>hasMore</c> + <c>cursor</c>) for <see cref="ResultKind.Page"/>.
/// </summary>
// begin-snippet: wireResponse
public sealed record QueryResponse(int Version, ResultKind Kind, JsonElement Payload)
{
    /// <summary>Creates a response stamped with the current <see cref="WireFormat.Version"/>.</summary>
    public static QueryResponse Create(ResultKind kind, JsonElement payload) =>
        new(WireFormat.Version, kind, payload);

    /// <summary>
    /// Renamed enum values ([PreviousNames] on the server model), sent only when the request's schema
    /// stamp differs from the server's. Lets a client generated before a rename resolve a value name
    /// it does not know to one it does. Null otherwise, and omitted from the JSON.
    /// </summary>
    public IReadOnlyList<EnumAlias>? EnumAliases { get; init; }
}
// end-snippet
