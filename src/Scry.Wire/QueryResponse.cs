namespace Scry;

/// <summary>
/// A serialized query result. <see cref="Payload"/> is a JSON array of projected rows for
/// <see cref="ResultKind.List"/>, a single projected object for <see cref="ResultKind.Single"/>,
/// a scalar for <see cref="ResultKind.Scalar"/>, or a <see cref="ScryPage{T}"/> envelope
/// (<c>items</c> + <c>hasMore</c> + <c>cursor</c>) for <see cref="ResultKind.Page"/>.
/// </summary>
/// <remarks>
/// The member order is pinned rather than left to declaration order: these members are written in
/// this order today, the fast response writer reproduces those bytes exactly, and a golden test
/// compares the two — so the order is part of the wire rather than a rendering detail, and saying so
/// here keeps a later refactor from moving it silently.
/// </remarks>
// begin-snippet: wireResponse
public sealed partial record QueryResponse(
    [property: JsonPropertyOrder(0)] int Version,
    [property: JsonPropertyOrder(1)] ResultKind Kind,
    JsonElement Payload)
{
    /// <summary>Creates a response stamped with the current <see cref="WireFormat.Version"/>.</summary>
    public static QueryResponse Create(ResultKind kind, JsonElement payload) =>
        new(WireFormat.Version, kind, payload);

    /// <summary>
    /// The server's schema stamp, carried on every successful response so a client can compare it
    /// against its own and detect a drifted model. The HTTP transport also advertises it as a header
    /// (<see cref="WireFormat.SchemaStampHeader"/>), which additionally covers error responses; this is
    /// the channel every other transport uses.
    /// </summary>
    [JsonPropertyOrder(3)]
    public string? Stamp { get; init; }

    /// <summary>
    /// Renamed enum values ([PreviousNames] on the server model), sent only when the request's schema
    /// stamp differs from the server's. Lets a client generated before a rename resolve a value name
    /// it does not know to one it does. Null otherwise, and omitted from the JSON.
    /// </summary>
    [JsonPropertyOrder(4)]
    public IReadOnlyList<EnumAlias>? EnumAliases { get; init; }

    /// <summary>
    /// The raw binary parts a <see cref="ScryBinary.ContentType"/> response arrived with, in wire
    /// order, set by the transport for <c>ScryJson.DeserializePayload</c> to resolve placeholders
    /// against. Never serialized — the parts travel beside the JSON, not inside it.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<byte[]>? BinaryParts { get; init; }
}
// end-snippet
