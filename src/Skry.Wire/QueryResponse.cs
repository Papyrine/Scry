using System.Text.Json;

namespace Skry.Wire;

/// <summary>
/// A serialized query result. <see cref="Payload"/> is a JSON array of projected rows for
/// <see cref="ResultKind.List"/>, a single projected object for <see cref="ResultKind.Single"/>,
/// or a scalar for <see cref="ResultKind.Scalar"/>.
/// </summary>
public sealed record QueryResponse(int Version, ResultKind Kind, JsonElement Payload)
{
    /// <summary>Creates a response stamped with the current <see cref="WireFormat.Version"/>.</summary>
    public static QueryResponse Create(ResultKind kind, JsonElement payload) =>
        new(WireFormat.Version, kind, payload);
}
