namespace Scry;

/// <summary>
/// Records every Scry exchange on the client it is attached to into <see cref="ScrySidecarStore"/>,
/// for the <see cref="ScrySidecar"/> panel. Attach it to the named client Scry uses:
/// <c>services.AddHttpClient("scry").AddHttpMessageHandler&lt;ScrySidecarHandler&gt;()</c>.
/// </summary>
/// <remarks>
/// Query and batch bodies are buffered — the client above buffers those itself, so nothing
/// observable changes. Streamed and attachment responses are passed through untouched: a stream is
/// meant to be read a row at a time and an attachment's bytes are handed back unbuffered, so for
/// both only status and headers are recorded. Capture itself never fails a working request: any
/// exception while recording is swallowed and the exchange proceeds as if the sidecar were absent.
/// </remarks>
public sealed class ScrySidecarHandler(ScrySidecarStore store, ScrySidecarOptions options) :
    DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        Cancel cancellationToken)
    {
        if (!options.Enabled)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var kind = Classify(request);
        var entry = await CaptureRequest(request, kind, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            Record(entry with {Duration = stopwatch.Elapsed, Error = exception.Message});
            throw;
        }

        // A stream is read row by row above this handler and an attachment's bytes flow through
        // unbuffered, so neither body can be captured without breaking the caller.
        if (kind is ScrySidecarKind.Stream or ScrySidecarKind.Attachment or ScrySidecarKind.Other)
        {
            Record(WithResponse(entry, response) with {Duration = stopwatch.Elapsed});
            return response;
        }

        byte[] body;
        try
        {
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Record(WithResponse(entry, response) with {Duration = stopwatch.Elapsed, Error = exception.Message});
            throw;
        }

        entry = WithResponse(entry, response) with {Duration = stopwatch.Elapsed};
        Record(await CaptureBody(entry, response, body, cancellationToken));

        // The content has been read to the end, so the response is handed back over the bytes
        // rather than over the stream they came out of.
        var content = new ByteArrayContent(body);
        foreach (var header in response.Content.Headers)
        {
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content.Dispose();
        response.Content = content;
        return response;
    }

    // Recording must never turn a working query into a failure, so every capture path lands here.
    void Record(ScrySidecarEntry entry)
    {
        try
        {
            store.Add(entry);
        }
        catch
        {
            // A debug log that cannot record has nothing useful to do about it.
        }
    }

    static async Task<ScrySidecarEntry> CaptureRequest(HttpRequestMessage request, ScrySidecarKind kind, Cancel cancel)
    {
        var entry = new ScrySidecarEntry
        {
            Id = 0,
            Started = DateTimeOffset.Now,
            Duration = TimeSpan.Zero,
            Method = request.Method.Method,
            Url = request.RequestUri?.ToString() ?? "",
            Kind = kind,
            RequestHeaders = Flatten(request.Headers, request.Content?.Headers)
        };

        try
        {
            if (kind == ScrySidecarKind.Query &&
                request.Method == HttpMethod.Get &&
                Encoded(request.RequestUri) is { } encoded)
            {
                var decoded = QueryUrl.Decode(encoded);
                return entry with
                {
                    Request = decoded,
                    RequestJson = SidecarJson.Prettify(ScryJson.Serialize(decoded))
                };
            }

            // Safe to read: ScryClient sends JSON bodies as ByteArrayContent, which re-reads.
            if (request.Content is not null &&
                kind is ScrySidecarKind.Query or ScrySidecarKind.Batch or ScrySidecarKind.Attachment)
            {
                var body = await request.Content.ReadAsByteArrayAsync(cancel);
                return entry with
                {
                    Request = kind == ScrySidecarKind.Query ? ScryJson.DeserializeRequest(body) : null,
                    RequestJson = SidecarJson.Prettify(body),
                    AttachmentRequestBody = kind == ScrySidecarKind.Attachment ? body : null
                };
            }
        }
        catch
        {
            // An undecodable request is still worth listing; the panes it cannot fill stay empty.
        }

        return entry;
    }

    static ScrySidecarEntry WithResponse(ScrySidecarEntry entry, HttpResponseMessage response) =>
        entry with
        {
            Status = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            ResponseHeaders = Flatten(response.Headers, response.Content.Headers)
        };

    static async Task<ScrySidecarEntry> CaptureBody(
        ScrySidecarEntry entry,
        HttpResponseMessage response,
        byte[] body,
        Cancel cancel)
    {
        try
        {
            if (MultipartResponse.TryGetBoundary(response, out var boundary))
            {
                var reader = new MultipartReader(boundary, new MemoryStream(body));
                var sizes = new List<int>();
                byte[]? envelope = null;
                while (await reader.ReadNextSectionAsync(cancel) is { } section)
                {
                    var part = await MultipartResponse.ReadPartBytes(section, cancel);
                    if (MultipartResponse.IsBinary(section))
                    {
                        sizes.Add(part.Length);
                    }
                    else
                    {
                        envelope = part;
                    }
                }

                return entry with
                {
                    ResponseJson = envelope is null ? null : SidecarJson.Prettify(envelope),
                    BinaryPartSizes = sizes
                };
            }

            entry = entry with {ResponseJson = SidecarJson.Prettify(body)};
            if (!response.IsSuccessStatusCode &&
                ScryJson.TryDeserializeError(body) is { } error)
            {
                entry = entry with {Error = error.Error};
            }

            return entry;
        }
        catch
        {
            return entry;
        }
    }

    static ScrySidecarKind Classify(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/attachment", StringComparison.Ordinal))
        {
            return ScrySidecarKind.Attachment;
        }

        if (path.EndsWith("/batch", StringComparison.Ordinal))
        {
            return ScrySidecarKind.Batch;
        }

        if (path.EndsWith("/stream", StringComparison.Ordinal))
        {
            return ScrySidecarKind.Stream;
        }

        if (request.Method == HttpMethod.Get &&
            Encoded(request.RequestUri) is not null)
        {
            return ScrySidecarKind.Query;
        }

        if (request.Method == HttpMethod.Post &&
            request.Content?.Headers.ContentType?.MediaType == "application/json")
        {
            return ScrySidecarKind.Query;
        }

        return ScrySidecarKind.Other;
    }

    /// <summary>The URL's <see cref="QueryUrl.Parameter"/> value, when present.</summary>
    static string? Encoded(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Query.Length == 0)
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith(QueryUrl.Parameter + '=', StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(QueryUrl.Parameter.Length + 1)..]);
            }
        }

        return null;
    }

    static IReadOnlyList<KeyValuePair<string, string>> Flatten(HttpHeaders headers, HttpContentHeaders? content)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var header in headers)
        {
            list.Add(new(header.Key, string.Join(", ", header.Value)));
        }

        if (content is not null)
        {
            foreach (var header in content)
            {
                list.Add(new(header.Key, string.Join(", ", header.Value)));
            }
        }

        return list;
    }
}
