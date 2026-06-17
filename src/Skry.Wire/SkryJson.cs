using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skry.Wire;

/// <summary>
/// Centralized, cached <see cref="JsonSerializerOptions"/> and fail-closed (de)serialization for the
/// wire format. Enum values are written as stable names; unknown type discriminators throw.
/// </summary>
public static class SkryJson
{
    /// <summary>The shared serializer options. Stable across versions — changing these is a wire break.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Result payloads are shaped as dictionaries; keep their keys consistent with property
            // naming so the client materializes them. Case-insensitive matching is belt-and-braces.
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        // No naming policy on enums: names are part of the wire contract and must stay stable.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize(QueryRequest request) =>
        JsonSerializer.Serialize(request, Options);

    public static string Serialize(QueryResponse response) =>
        JsonSerializer.Serialize(response, Options);

    public static QueryRequest DeserializeRequest(string json) =>
        Deserialize<QueryRequest>(json, "request");

    public static QueryResponse DeserializeResponse(string json) =>
        Deserialize<QueryResponse>(json, "response");

    static T Deserialize<T>(string json, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ??
                   throw new SkryWireException($"Query {what} deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new SkryWireException($"Invalid query {what}: {exception.Message}", exception);
        }
    }
}
