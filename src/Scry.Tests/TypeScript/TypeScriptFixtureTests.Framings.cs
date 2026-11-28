using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

public partial class TypeScriptFixtureTests
{
    // The headers a client reads off a response. Everything else a host adds is the host's.
    static string[] readHeaders =
    [
        "Content-Type",
        "Cache-Control",
        "ETag",
        "Vary",
        WireFormat.SchemaStampHeader,
        WireFormat.UrlLimitHeader
    ];

    /// <summary>
    /// What each HTTP endpoint answers, as it arrives: the status, the headers a client reads, and the
    /// body. These are the framings an envelope in the request corpus does not show — the URL form, a
    /// stream's rows between their markers, a live query's first event, the multipart a binary member
    /// travels in, a batch, and the error bodies.
    /// </summary>
    /// <remarks>
    /// The schema stamp is written as <c>{stamp}</c>, a live result's id as <c>{fingerprint}</c>, and a
    /// multipart boundary as <c>scry</c> and zeros: each is opaque to a client, the stamp and the
    /// fingerprint change with every change to the test model, and the boundary with every response. A
    /// body holding binary parts is kept as base64, since its parts are bytes and not text.
    /// </remarks>
    [Test]
    public async Task Framings()
    {
        var app = await Host();
        try
        {
            using var http = app.GetTestClient();

            var list = Capture(_ => _.Source<Employee>("Employee").Where(_ => _.Active).OrderBy(_ => _.Name).Select(_ => new {_.Name}).ToListAsync());
            var binary = Capture(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Id).Take(2).Select(_ => new {_.Name, _.Avatar}).ToListAsync());
            var avatar = new byte[] {0x01, 0x02, 0x03};
            var sensitive = Capture(_ => _.Source<Employee>("Employee").Where(_ => _.Avatar == avatar).Select(_ => new {_.Name}).ToListAsync());
            var refused = QueryRequest.Create("Order", [new SelectOp(new([new("Revision", new NodeValue(new MemberNode(["Revision"])))]))]);
            var batch = QueryBatchRequest.Create([list, refused]);

            Exchange[] exchanges =
            [
                await Send(http, "A list, as a body", HttpMethod.Post, "/api/query", ScryJson.Serialize(list)),
                await SendAsUrl(http, "The same list, as a URL", list),
                await Send(http, "A stream", HttpMethod.Post, "/api/query/stream", ScryJson.Serialize(list)),
                await Send(http, "A batch, one entry refused", HttpMethod.Post, "/api/query/batch", ScryJson.Serialize(batch)),
                await Send(http, "A binary member, as multipart", HttpMethod.Post, "/api/query", ScryJson.Serialize(binary)),
                await Send(http, "A binary member, streamed", HttpMethod.Post, "/api/query/stream", ScryJson.Serialize(binary)),
                await Send(http, "A refusal", HttpMethod.Post, "/api/query", ScryJson.Serialize(refused)),
                await Send(http, "A request that does not parse", HttpMethod.Post, "/api/query", """{"version":1,"root":"Employee","pipeline":[{"$type":"nope"}]}"""),
                await SendAsUrl(http, "A sensitive comparison, as a URL", sensitive),
                await FirstEvent(http, "A live query's first event", ScryJson.Serialize(list))
            ];

            var json = Document(writer =>
            {
                writer.WriteStartArray();
                foreach (var exchange in exchanges)
                {
                    exchange.Write(writer);
                }

                writer.WriteEndArray();
            });

            await Verify(json, "json")
                .UseFileName("framings")
                .NotInline();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    static async Task<WebApplication> Host()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => TestContext.CreateSeeded());
        builder.Services.AddScry<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.MaxSubscriptions = 1;
            // The test model opts in a source with no table, to pin how it is named; startup would
            // otherwise refuse the model for it.
            options.AllowUnmappedSources = true;
        });

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }

    static async Task<Exchange> Send(HttpClient http, string name, HttpMethod method, string path, string? body, params string[] more)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonBody(body);
        }

        using var response = await http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return Exchange.From(name, method, path, body, response, bytes, more);
    }

    // Recorded with the request as the body it encodes and the parameter as what it is made from,
    // rather than as the encoded text: the URL form is base64url of exactly those bytes, so a client
    // that writes the body writes the URL, and the encoded text alone would say neither.
    static async Task<Exchange> SendAsUrl(HttpClient http, string name, QueryRequest query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/query?{QueryUrl.Parameter}={QueryUrl.Encode(query)}");
        using var response = await http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return Exchange.From(name, HttpMethod.Get, $"/api/query?{QueryUrl.Parameter}={{base64url(requestBody)}}", ScryJson.Serialize(query), response, bytes);
    }

    // A live query never ends on its own, so only its first event is read, and the connection is then
    // dropped.
    static async Task<Exchange> FirstEvent(HttpClient http, string name, string body)
    {
        var path = $"/api/query/{ScryLive.Route}";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = JsonBody(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancel.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                line = "id: {fingerprint}";
            }

            text.Append(line);
            text.Append('\n');
            if (line.Length == 0)
            {
                break;
            }
        }

        return Exchange.From(name, HttpMethod.Post, path, body, response, Encoding.UTF8.GetBytes(text.ToString()));
    }

    // As the client sends a body.
    static ByteArrayContent JsonBody(string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }

    // What a query sends, recorded at the transport and stopped there.
    static QueryRequest Capture(Func<ScryClient, Task> query)
    {
        QueryRequest? sent = null;
        var client = new ScryClient(
            (request, _) =>
            {
                sent = request;
                throw new StopBeforeSending();
            });

        try
        {
            query(client).GetAwaiter().GetResult();
        }
        catch (StopBeforeSending)
        {
        }

        return sent ?? throw new("The query sent nothing.");
    }

    sealed class StopBeforeSending :
        Exception;

    sealed record Exchange(
        string Name,
        string Method,
        string Path,
        string? RequestBody,
        int Status,
        IReadOnlyList<(string Name, string Value)> Headers,
        string? Body,
        string? BodyBase64)
    {
        public static Exchange From(string name, HttpMethod method, string path, string? requestBody, HttpResponseMessage response, byte[] bytes, params string[] more)
        {
            var stamp = Header(response, WireFormat.SchemaStampHeader);
            var contentType = Header(response, "Content-Type");
            var boundary = Boundary(contentType);

            var headers = new List<(string Name, string Value)>();
            foreach (var header in readHeaders.Concat(more))
            {
                if (Header(response, header) is { } value)
                {
                    headers.Add((header, Opaque(value, stamp, boundary)));
                }
            }

            bytes = Opaque(bytes, stamp, boundary);
            if (contentType is not null &&
                !Textual(contentType))
            {
                return new(name, method.Method, path, requestBody, (int) response.StatusCode, headers, null, Convert.ToBase64String(bytes));
            }

            return new(name, method.Method, path, requestBody, (int) response.StatusCode, headers, Encoding.UTF8.GetString(bytes), null);
        }

        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);
            writer.WriteString("method", Method);
            writer.WriteString("path", Path);
            if (RequestBody is not null)
            {
                writer.WriteString("requestBody", RequestBody);
            }

            writer.WriteNumber("status", Status);
            writer.WriteStartObject("headers");
            foreach (var (header, value) in Headers)
            {
                writer.WriteString(header, value);
            }

            writer.WriteEndObject();
            if (Body is not null)
            {
                writer.WriteString("body", Body);
            }

            if (BodyBase64 is not null)
            {
                writer.WriteString("bodyBase64", BodyBase64);
            }

            writer.WriteEndObject();
        }

        // A body of bytes rather than text, kept as base64: multipart, whose parts are bytes, and an
        // attachment served as whatever its member declares.
        static bool Textual(string contentType) =>
            contentType.StartsWith("application/json", StringComparison.Ordinal) ||
            contentType.StartsWith(ScryStream.ContentType, StringComparison.Ordinal) ||
            contentType.StartsWith(ScryLive.ContentType, StringComparison.Ordinal) ||
            contentType.StartsWith("text/", StringComparison.Ordinal);

        static string? Header(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values) ||
                response.Content.Headers.TryGetValues(name, out values))
            {
                return string.Join(", ", values);
            }

            return null;
        }

        static string? Boundary(string? contentType)
        {
            const string marker = "boundary=";
            var index = contentType?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
            if (index < 0)
            {
                return null;
            }

            return contentType![(index + marker.Length)..].Split(';')[0].Trim('"');
        }

        static string Opaque(string value, string? stamp, string? boundary) =>
            Encoding.UTF8.GetString(Opaque(Encoding.UTF8.GetBytes(value), stamp, boundary));

        static byte[] Opaque(byte[] bytes, string? stamp, string? boundary)
        {
            if (stamp is not null)
            {
                bytes = Replace(bytes, stamp, "{stamp}");
            }

            if (boundary is not null)
            {
                bytes = Replace(bytes, boundary, ScryBinary.BoundaryPrefix + new string('0', boundary.Length - ScryBinary.BoundaryPrefix.Length));
            }

            return bytes;
        }

        // Both are ASCII, so they are found as bytes, and a binary part around them is left as it was.
        static byte[] Replace(byte[] bytes, string find, string replacement)
        {
            var needle = Encoding.ASCII.GetBytes(find);
            var with = Encoding.ASCII.GetBytes(replacement);
            var result = new List<byte>(bytes.Length);
            var span = bytes.AsSpan();
            while (true)
            {
                var index = span.IndexOf(needle);
                if (index < 0)
                {
                    result.AddRange(span);
                    return result.ToArray();
                }

                result.AddRange(span[..index]);
                result.AddRange(with);
                span = span[(index + needle.Length)..];
            }
        }
    }
}
