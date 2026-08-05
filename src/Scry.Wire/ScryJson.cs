namespace Scry;

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
        // Same shape of addition for binary: byte-identical to the built-in base64 handling except
        // when a payload read hits a multipart placeholder — see DeserializePayload.
        options.Converters.Add(new BinaryConverter());

        // The wire vocabulary is closed, so all of it is generated at compile time and answered here
        // without reflecting over a type. A payload's type is the one thing this assembly cannot know
        // — it is the consumer's generated query model, an anonymous projection, or a DTO of theirs —
        // so reflection sits behind the generated set and only ever sees what the wire does not name.
        options.TypeInfoResolverChain.Add(WireJsonContext.Default);
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        return options;
    }

    // Resolved once, through Options, so each carries the shared policies and converters and every
    // method below hands JsonSerializer its metadata rather than looking one up by type per call.
    // Declared after Options because static field initializers run in textual order.
    static readonly JsonTypeInfo<QueryRequest> requestInfo = Info<QueryRequest>();
    static readonly JsonTypeInfo<QueryResponse> responseInfo = Info<QueryResponse>();
    static readonly JsonTypeInfo<QueryBatchRequest> batchRequestInfo = Info<QueryBatchRequest>();
    static readonly JsonTypeInfo<QueryBatchResponse> batchResponseInfo = Info<QueryBatchResponse>();
    static readonly JsonTypeInfo<ScryIntrospection> introspectionInfo = Info<ScryIntrospection>();
    static readonly JsonTypeInfo<ScryStreamMarker> markerInfo = Info<ScryStreamMarker>();
    static readonly JsonTypeInfo<ScryError> errorInfo = Info<ScryError>();

    static JsonTypeInfo<T> Info<T>() =>
        (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

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
        // Null when the response was not multipart: a placeholder can then only fail closed.
        BinaryPartScope.Current = response.BinaryParts;
        try
        {
            return response.Payload.Deserialize<T>(Options);
        }
        finally
        {
            EnumAliasScope.Current = null;
            BinaryPartScope.Current = null;
        }
    }

    /// <summary>
    /// Deserializes one row of a streamed result. The streaming counterpart of
    /// <see cref="DeserializePayload{T}"/>: a stream carries its enum aliases once, on the opening
    /// marker, so they are passed per row instead of read off a response.
    /// </summary>
    public static T? DeserializeRow<T>(JsonElement row, IReadOnlyList<EnumAlias>? aliases, IReadOnlyList<byte[]>? parts = null)
    {
        EnumAliasScope.Current = aliases ?? [];
        // A stream carries binary parts per row, so they are passed per row like the aliases.
        BinaryPartScope.Current = parts;
        try
        {
            return row.Deserialize<T>(Options);
        }
        finally
        {
            EnumAliasScope.Current = null;
            BinaryPartScope.Current = null;
        }
    }

    public static string Serialize(QueryRequest request) =>
        JsonSerializer.Serialize(request, requestInfo);

    public static string Serialize(QueryResponse response) =>
        JsonSerializer.Serialize(response, responseInfo);

    public static string Serialize(ScryIntrospection introspection) =>
        JsonSerializer.Serialize(introspection, introspectionInfo);

    public static string Serialize(QueryBatchRequest request) =>
        JsonSerializer.Serialize(request, batchRequestInfo);

    public static string Serialize(QueryBatchResponse response) =>
        JsonSerializer.Serialize(response, batchResponseInfo);

    /// <summary>Writes one line of a streamed result — an opening or closing marker.</summary>
    public static string Serialize(ScryStreamMarker marker) =>
        JsonSerializer.Serialize(marker, markerInfo);

    public static QueryRequest DeserializeRequest([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Deserialize(json, requestInfo, "request");

    public static QueryBatchRequest DeserializeBatchRequest([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Deserialize(json, batchRequestInfo, "batch request");

    /// <summary>Reads a server's introspection document.</summary>
    public static ScryIntrospection DeserializeIntrospection([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Deserialize(json, introspectionInfo, "introspection");

    /// <summary>
    /// Reads one line of a streamed result as a marker. The caller has already established that the
    /// line carries <see cref="ScryStream.MarkerProperty"/>, so this is not a probe.
    /// </summary>
    public static ScryStreamMarker DeserializeMarker(JsonElement line) =>
        line.Deserialize(markerInfo) ??
        throw new ScryWireException("Stream marker deserialized to null.");

    /// <summary>
    /// Reads a non-success response body, or null when it is not one. A failed request is usually
    /// answered with the endpoint's own <see cref="ScryError"/>, but may be answered by anything once a
    /// proxy or other middleware is in the way — so an unreadable body is a "not one of ours" rather
    /// than a failure of its own.
    /// </summary>
    public static ScryError? TryDeserializeError([StringSyntax(StringSyntaxAttribute.Json)] string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, errorInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static QueryBatchResponse DeserializeBatchResponse([StringSyntax(StringSyntaxAttribute.Json)] string json)
    {
        var response = Deserialize(json, batchResponseInfo, "batch response");
        // The same gate DeserializeResponse applies: a batch shaped by a newer wire format than this
        // client understands is refused rather than misread.
        if (response.Version <= WireFormat.Version)
        {
            return response;
        }

        throw new ScryWireException($"Unsupported response wire version {response.Version}; this client supports up to {WireFormat.Version}. The server is newer than the client.");
    }

    public static QueryResponse DeserializeResponse([StringSyntax(StringSyntaxAttribute.Json)] string json)
    {
        var response = Deserialize(json, responseInfo, "response");
        // Mirror the server's request-version gate (QueryValidator): reject a response stamped with a
        // newer wire format than this client understands rather than misreading a payload shaped by a
        // format it was not built against.
        if (response.Version <= WireFormat.Version)
        {
            return response;
        }

        throw new ScryWireException($"Unsupported response wire version {response.Version}; this client supports up to {WireFormat.Version}. The server is newer than the client.");
    }

    static T Deserialize<T>([StringSyntax(StringSyntaxAttribute.Json)] string json, JsonTypeInfo<T> info, string what)
    {
        try
        {
            return JsonSerializer.Deserialize(json, info) ??
                   throw new ScryWireException($"Query {what} deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new ScryWireException($"Invalid query {what}: {exception.Message}", exception);
        }
    }
}
