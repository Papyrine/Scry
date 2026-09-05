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
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // A null where the vocabulary declares a value is refused on read. Options-wide, which is
            // wider than wanted — see RelaxAnnotations for where it is taken back.
            RespectNullableAnnotations = true
        };
        // No naming policy on enums: names are part of the wire contract and must stay stable. The
        // tolerant wrapper is byte-identical to JsonStringEnumConverter except when a payload read
        // hits a value name this side does not know — see DeserializePayload.
        options.Converters.Add(new TolerantEnumConverterFactory());
        // Same shape of addition for binary: byte-identical to the built-in base64 handling except
        // when a payload read hits a multipart placeholder — see DeserializePayload.
        options.Converters.Add(new BinaryConverter());
        // A null element of a wire array — a pipeline entry, a group key, a call argument — is refused
        // here, where a null member already is; RespectNullableAnnotations does not reach elements.
        options.Converters.Add(new NonNullElementsConverterFactory());

        // The wire vocabulary is closed, so all of it is generated at compile time and answered here
        // without reflecting over a type. A payload's type is the one thing this assembly cannot know
        // — it is the consumer's generated query model, an anonymous projection, or a DTO of theirs —
        // so reflection sits behind the generated set and only ever sees what the wire does not name.
        options.TypeInfoResolverChain.Add(WireJsonContext.Default.WithAddedModifier(RequireWhatTheWriterAlwaysWrites));
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver {Modifiers = {RelaxAnnotations}});
        return options;
    }

    // A member the vocabulary requires — a where's predicate, a request's root — is a deserialization
    // failure when absent, rather than its default handed to a validator that then dereferences it.
    // The members the writer omits when null are the ones declared optional, so a request this side
    // writes always reads back. Together with the nullability switch above, a required member is
    // present and holds a value: an explicit null is the absence's twin, and is refused the same way.
    static void RequireWhatTheWriterAlwaysWrites(JsonTypeInfo type)
    {
        foreach (var property in type.Properties)
        {
            if (property.AssociatedParameter is {HasDefaultValue: false, IsMemberInitializer: false})
            {
                property.IsRequired = true;
            }
        }

        // A request names only what the vocabulary names. A member nothing reads is refused rather
        // than skipped: skipping is what let a form field shaped as JSON carry its "=" in a member
        // the server never looked at, and a request is the one side of the wire with no reader that
        // could be older than its writer. A response stays lenient — a client may be.
        if (IsRequest(type.Type))
        {
            type.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        }
    }

    static bool IsRequest(Type type) =>
        typeof(QueryOp).IsAssignableFrom(type) ||
        typeof(Node).IsAssignableFrom(type) ||
        typeof(ProjectionValue).IsAssignableFrom(type) ||
        type == typeof(QueryRequest) ||
        type == typeof(QueryBatchRequest) ||
        type == typeof(AttachmentRequest) ||
        type == typeof(AttachmentKey) ||
        type == typeof(JoinMember) ||
        type == typeof(Projection);

    // Everything the vocabulary does not name reads and writes null as it always did. The switch
    // above is options-wide, but a consumer's row type is theirs: a member declared non-null is
    // handed a null where a policy hid the row behind a navigation, and an attachment handle is
    // filled in by the client after the read rather than carried by the payload. Only a member that
    // can hold a null is touched — the serializer refuses to relax a value type.
    static void RelaxAnnotations(JsonTypeInfo type)
    {
        foreach (var property in type.Properties)
        {
            if (!property.PropertyType.IsValueType ||
                Nullable.GetUnderlyingType(property.PropertyType) is not null)
            {
                property.IsGetNullable = true;
                property.IsSetNullable = true;
            }
        }
    }

    // Resolved once, through Options, so each carries the shared policies and converters and every
    // method below hands JsonSerializer its metadata rather than looking one up by type per call.
    // Declared after Options because static field initializers run in textual order.
    static readonly JsonTypeInfo<QueryRequest> requestInfo = Info<QueryRequest>();
    static readonly JsonTypeInfo<QueryResponse> responseInfo = Info<QueryResponse>();
    static readonly JsonTypeInfo<AttachmentRequest> attachmentRequestInfo = Info<AttachmentRequest>();
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
            // Read from the bytes the transport kept, when it kept them: the payload is parsed once,
            // straight into T. Going through Payload instead means parsing it into a document, writing
            // that document back out to a buffer, and parsing the buffer — for a document that exists
            // only to be spent here.
            if (!response.RawPayload.IsEmpty)
            {
                return JsonSerializer.Deserialize<T>(response.RawPayload.Span, Options);
            }

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

    /// <summary>
    /// Reads one row of a streamed result from the UTF-8 line it arrived as. The same as the
    /// <see cref="JsonElement"/> overload in every respect but what it starts from — a transport
    /// holding the line's bytes has no reason to build a document out of them first, only for the row
    /// to be written back out and re-read.
    /// </summary>
    public static T? DeserializeRow<T>(ReadOnlySpan<byte> row, IReadOnlyList<EnumAlias>? aliases, IReadOnlyList<byte[]>? parts = null)
    {
        EnumAliasScope.Current = aliases ?? [];
        BinaryPartScope.Current = parts;
        try
        {
            return JsonSerializer.Deserialize<T>(row, Options);
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

    public static string Serialize(AttachmentRequest request) =>
        JsonSerializer.Serialize(request, attachmentRequestInfo);

    public static string Serialize(ScryIntrospection introspection) =>
        JsonSerializer.Serialize(introspection, introspectionInfo);

    public static string Serialize(QueryBatchRequest request) =>
        JsonSerializer.Serialize(request, batchRequestInfo);

    // The serializer writes UTF-8 natively and a transport sends UTF-8, so the string overloads above
    // transcode to UTF-16 only for the transport to transcode straight back. These skip both passes and
    // the string that existed to be discarded — and hand the caller the exact bytes that go on the wire,
    // which is what a caller fingerprinting a request wants to hash.
    public static byte[] SerializeToUtf8(QueryRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, requestInfo);

    public static byte[] SerializeToUtf8(QueryResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, responseInfo);

    public static byte[] SerializeToUtf8(AttachmentRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, attachmentRequestInfo);

    public static byte[] SerializeToUtf8(QueryBatchRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, batchRequestInfo);

    public static byte[] SerializeToUtf8(QueryBatchResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, batchResponseInfo);

    public static string Serialize(QueryBatchResponse response) =>
        JsonSerializer.Serialize(response, batchResponseInfo);

    /// <summary>
    /// Writes a response into a writer that is already mid-document. What a batch envelope written
    /// row-by-row falls back to for an entry the row writer cannot produce — a terminal result, or the
    /// alias-carrying envelope a drifted client is answered with — so a batch mixing the two shapes is
    /// still written in one pass rather than one of them being serialized to a buffer of its own.
    /// </summary>
    public static void Write(Utf8JsonWriter writer, QueryResponse response) =>
        JsonSerializer.Serialize(writer, response, responseInfo);

    /// <summary>Writes one line of a streamed result — an opening or closing marker.</summary>
    public static string Serialize(ScryStreamMarker marker) =>
        JsonSerializer.Serialize(marker, markerInfo);

    public static QueryRequest DeserializeRequest([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Deserialize(json, requestInfo, "request");

    public static QueryBatchRequest DeserializeBatchRequest([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Deserialize(json, batchRequestInfo, "batch request");

    /// <summary>
    /// Reads a request for an attachment's bytes. The version it carries is checked by the server as
    /// it is for a query: this only refuses what is not readable as the shape at all.
    /// </summary>
    public static AttachmentRequest DeserializeAttachmentRequest([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Deserialize(json, attachmentRequestInfo, "attachment request");

    // The mirror of SerializeToUtf8: a body arrives as UTF-8 and the reader wants UTF-8, so decoding it
    // to a string on the way in transcodes twice for nothing. These read the received bytes directly.
    public static QueryRequest DeserializeRequest(ReadOnlySpan<byte> utf8) =>
        Deserialize(utf8, requestInfo, "request");

    public static QueryBatchRequest DeserializeBatchRequest(ReadOnlySpan<byte> utf8) =>
        Deserialize(utf8, batchRequestInfo, "batch request");

    public static AttachmentRequest DeserializeAttachmentRequest(ReadOnlySpan<byte> utf8) =>
        Deserialize(utf8, attachmentRequestInfo, "attachment request");

    /// <summary>Reads a server's introspection document.</summary>
    public static ScryIntrospection DeserializeIntrospection([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Deserialize(json, introspectionInfo, "introspection");

    /// <summary>
    /// Reads one line of a streamed result as a marker. The caller has already established that the
    /// line carries <see cref="ScryStream.MarkerProperty"/>, so this is not a probe.
    /// </summary>
    public static ScryStreamMarker DeserializeMarker(JsonElement line) =>
        Versioned(
            line.Deserialize(markerInfo) ??
            throw new ScryWireException("Stream marker deserialized to null."));

    /// <summary>Reads a marker from the UTF-8 line it arrived as.</summary>
    public static ScryStreamMarker DeserializeMarker(ReadOnlySpan<byte> line) =>
        Versioned(
            JsonSerializer.Deserialize(line, markerInfo) ??
            throw new ScryWireException("Stream marker deserialized to null."));

    // An opening marker carrying a newer wire version fails closed as a response does: the rows
    // behind it are in an encoding this client does not read, and reading them anyway would answer
    // with wrong rows or a bare parse failure rather than saying the server is newer.
    static ScryStreamMarker Versioned(ScryStreamMarker marker)
    {
        if (marker is {Kind: ScryStream.Begin, Version: { } version and > WireFormat.Version})
        {
            throw Unsupported(version);
        }

        return marker;
    }

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

    /// <summary>Reads a non-success response body from the UTF-8 it arrived as, or null when it is not one.</summary>
    public static ScryError? TryDeserializeError(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, errorInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static QueryBatchResponse DeserializeBatchResponse([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Versioned(Deserialize(json, batchResponseInfo, "batch response"));

    /// <summary>
    /// Reads a batch response from the UTF-8 it arrived as, keeping each entry's payload bytes for
    /// <see cref="DeserializePayload{T}"/> — see the single-response overload for what that saves.
    /// </summary>
    public static QueryBatchResponse DeserializeBatchResponse(ReadOnlyMemory<byte> utf8)
    {
        var (response, ranges) = ReadEnvelope(utf8, batchResponseInfo, "batch response");
        Versioned(response);
        if (ranges.Count == 0)
        {
            return response;
        }

        // JSON is read front to back and only an entry that succeeded has a payload to step over, so
        // the ranges line up with the entries carrying a response, in order.
        var results = new List<QueryBatchResult>(response.Results.Count);
        var next = 0;
        foreach (var result in response.Results)
        {
            if (result.Response is { } entry &&
                next < ranges.Count)
            {
                results.Add(result with {Response = entry with {RawPayload = Slice(utf8, ranges[next++])}});
            }
            else
            {
                results.Add(result);
            }
        }

        return response with {Results = results};
    }

    public static QueryResponse DeserializeResponse([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        Versioned(Deserialize(json, responseInfo, "response"));

    /// <summary>
    /// Reads a response from the UTF-8 it arrived as, keeping the payload's bytes on the response
    /// rather than parsing them into a document. <see cref="DeserializePayload{T}"/> then reads the
    /// result out of them once, which is what a list or page result wants; a payload something asks
    /// <see cref="QueryResponse.Payload"/> for is parsed then, on first read.
    /// </summary>
    /// <remarks>
    /// The memory is held by the returned response, so it must not be written to afterwards — the
    /// transports pass a buffer they have just read and do not reuse.
    /// </remarks>
    public static QueryResponse DeserializeResponse(ReadOnlyMemory<byte> utf8)
    {
        var (response, ranges) = ReadEnvelope(utf8, responseInfo, "response");
        Versioned(response);
        if (ranges.Count == 1)
        {
            return response with {RawPayload = Slice(utf8, ranges[0])};
        }

        return response;
    }

    // Mirror the server's request-version gate (QueryValidator): reject a response stamped with a
    // newer wire format than this client understands rather than misreading a payload shaped by a
    // format it was not built against.
    static QueryResponse Versioned(QueryResponse response)
    {
        if (response.Version <= WireFormat.Version)
        {
            return response;
        }

        throw Unsupported(response.Version);
    }

    static QueryBatchResponse Versioned(QueryBatchResponse response)
    {
        if (response.Version <= WireFormat.Version)
        {
            return response;
        }

        throw Unsupported(response.Version);
    }

    static ScryWireException Unsupported(int version) =>
        new($"Unsupported response wire version {version}; this client supports up to {WireFormat.Version}. The server is newer than the client.");

    static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> utf8, (int Start, int End) range) =>
        utf8[range.Start..range.End];

    // Reads an envelope while stepping over the payloads inside it, returning where each one sat.
    static (T Value, List<(int Start, int End)> Ranges) ReadEnvelope<T>(
        ReadOnlyMemory<byte> utf8,
        JsonTypeInfo<T> info,
        string what)
    {
        PayloadRangeScope.Begin();
        try
        {
            var value = Deserialize(utf8.Span, info, what);
            return (value, PayloadRangeScope.End());
        }
        finally
        {
            // Ends the scope on the throwing paths. Idempotent, so the success path above has already
            // taken the ranges and this does nothing.
            PayloadRangeScope.End();
        }
    }

    static T Deserialize<T>([StringSyntax(StringSyntaxAttribute.Json)] string json, JsonTypeInfo<T> info, string what)
    {
        try
        {
            var deserialize = JsonSerializer.Deserialize(json, info);
            if (deserialize == null)
            {
                throw new ScryWireException($"Query {what} deserialized to null.");
            }

            return deserialize;
        }
        catch (JsonException exception)
        {
            throw new ScryWireException($"Invalid query {what}: {exception.Message}", exception);
        }
    }

    // Duplicated rather than sharing a body with the string overload: a span cannot be captured, so the
    // two cannot funnel into one without buffering the very copy this exists to avoid.
    static T Deserialize<T>(ReadOnlySpan<byte> utf8, JsonTypeInfo<T> info, string what)
    {
        try
        {
            var deserialize = JsonSerializer.Deserialize(utf8, info);
            if (deserialize == null)
            {
                throw new ScryWireException($"Query {what} deserialized to null.");
            }

            return deserialize;
        }
        catch (JsonException exception)
        {
            throw new ScryWireException($"Invalid query {what}: {exception.Message}", exception);
        }
    }
}
