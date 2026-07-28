namespace Scry.Wire;

/// <summary>
/// Centralized, cached <see cref="JsonSerializerOptions"/> and fail-closed (de)serialization for the
/// wire format. Enum values are written as stable names; unknown type discriminators throw.
/// </summary>
public static class ScryJson
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
        // No naming policy on enums: names are part of the wire contract and must stay stable. The
        // tolerant wrapper is byte-identical to JsonStringEnumConverter except when a payload read
        // hits a value name this side does not know — see DeserializePayload.
        options.Converters.Add(new TolerantEnumConverterFactory());
        return options;
    }

    /// <summary>
    /// Deserializes a response's payload. Prefer this over deserializing <see cref="QueryResponse.Payload"/>
    /// directly: it makes the response's <see cref="QueryResponse.EnumAliases"/> available to the enum
    /// reader, so a client generated before an enum value rename can resolve the current name to the
    /// previous one it was generated with. An unresolvable name throws
    /// <see cref="ScryStaleClientException"/> rather than a bare <see cref="JsonException"/>.
    /// </summary>
    public static T? DeserializePayload<T>(QueryResponse response)
    {
        // Non-null marks "inside a payload" even when the response carried no aliases, so an unknown
        // enum name still reports a stale client instead of an unexplained parse failure.
        EnumAliasScope.Current = response.EnumAliases ?? [];
        try
        {
            return response.Payload.Deserialize<T>(Options);
        }
        finally
        {
            EnumAliasScope.Current = null;
        }
    }

    public static string Serialize(QueryRequest request) =>
        JsonSerializer.Serialize(request, Options);

    public static string Serialize(QueryResponse response) =>
        JsonSerializer.Serialize(response, Options);

    public static string Serialize(ScryIntrospection introspection) =>
        JsonSerializer.Serialize(introspection, Options);

    public static QueryRequest DeserializeRequest(string json) =>
        Deserialize<QueryRequest>(json, "request");

    public static QueryResponse DeserializeResponse(string json)
    {
        var response = Deserialize<QueryResponse>(json, "response");
        // Mirror the server's request-version gate (QueryValidator): reject a response stamped with a
        // newer wire format than this client understands rather than misreading a payload shaped by a
        // format it was not built against.
        if (response.Version > WireFormat.Version)
        {
            throw new ScryWireException(
                $"Unsupported response wire version {response.Version}; this client supports up to {WireFormat.Version}. The server is newer than the client.");
        }

        return response;
    }

    static T Deserialize<T>(string json, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ??
                   throw new ScryWireException($"Query {what} deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new ScryWireException($"Invalid query {what}: {exception.Message}", exception);
        }
    }
}
