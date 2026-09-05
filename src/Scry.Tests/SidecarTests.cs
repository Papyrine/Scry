/// <summary>
/// The sidecar's capture handler: what it records, and — just as important — what it refuses to
/// touch. Streams and attachments must pass through byte-identical and unbuffered, and capture
/// itself must never turn a working exchange into a failure.
/// </summary>
[TestFixture]
public class SidecarTests
{
    [ScryModel("Person", "Id", "Name", "Ssn")]
    public class PersonModel
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";

        [ScrySensitive]
        public string Ssn { get; init; } = "";
    }

    public record NameRow(string Name);

    [Test]
    public async Task GetQueryIsDecodedAndRecorded()
    {
        var (store, client) = Stubbed(_ => List());

        await Scry(client)
            .Where(_ => _.Id == 42)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        var entry = store.Entries.Single();
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Query));
        Assert.That(entry.Method, Is.EqualTo("GET"));
        Assert.That(entry.Request!.Root, Is.EqualTo("Person"));
        Assert.That(entry.RequestJson, Does.Contain("\"root\": \"Person\""));
        Assert.That(entry.Status, Is.EqualTo(200));
        Assert.That(entry.ResponseJson, Does.Contain("\"kind\""));
        // A bare Scry GET sets no request headers of its own, so only the response side has any.
        Assert.That(entry.ResponseHeaders.Select(_ => _.Key), Does.Contain("Content-Type"));
    }

    // A sensitive constant forces the query into a body; the body is exactly what the panel must
    // show to explain the exchange, so it is recorded like any other.
    [Test]
    public async Task SensitivePostBodyIsRecorded()
    {
        var (store, client) = Stubbed(_ => List());

        await Scry(client)
            .Where(_ => _.Ssn == "123-45-6789")
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        var entry = store.Entries.Single();
        Assert.That(entry.Method, Is.EqualTo("POST"));
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Query));
        Assert.That(entry.Request!.Root, Is.EqualTo("Person"));
        Assert.That(entry.RequestJson, Does.Contain("123-45-6789"));
    }

    [Test]
    public async Task BatchIsClassifiedAndBuffered()
    {
        var (store, client) = Stubbed(_ => Json("""{"version":1,"results":[]}"""));

        using var content = JsonContent("""{"version":1,"requests":[]}""");
        await client.PostAsync("/api/query/batch", content);

        var entry = store.Entries.Single();
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Batch));
        Assert.That(entry.RequestJson, Does.Contain("\"requests\""));
        Assert.That(entry.ResponseJson, Does.Contain("\"results\""));
    }

    // A stream is read a row at a time above the handler; buffering it here would stall the read
    // and defeat the endpoint. The content instance the caller gets must be the stub's own.
    [Test]
    public async Task StreamIsNotBuffered()
    {
        StreamContent? served = null;
        var (store, client) = Stubbed(
            _ =>
            {
                served = new(new MemoryStream("{}\n"u8.ToArray()));
                served.Headers.ContentType = new("application/x-ndjson");
                return new(HttpStatusCode.OK) {Content = served};
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/query/stream")
        {
            Content = JsonContent("{}")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.Content, Is.SameAs(served));
        var entry = store.Entries.Single();
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Stream));
        Assert.That(entry.ResponseJson, Is.Null);
        Assert.That(entry.Status, Is.EqualTo(200));
        Assert.That(entry.ResponseHeaders.Select(_ => _.Key), Does.Contain("Content-Type"));
    }

    // The download action re-sends the request, so the request body is kept; the response bytes
    // flow through untouched and are deliberately not.
    [Test]
    public async Task AttachmentKeepsTheRequestBodyAndPassesTheStreamThrough()
    {
        StreamContent? served = null;
        var (store, client) = Stubbed(
            _ =>
            {
                served = new(new MemoryStream([1, 2, 3]));
                served.Headers.ContentType = new("application/octet-stream");
                return new(HttpStatusCode.OK) {Content = served};
            });

        var body = """{"version":1,"root":"Person","member":"Photo","keys":[]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/query/attachment")
        {
            Content = JsonContent(body)
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.Content, Is.SameAs(served));
        var entry = store.Entries.Single();
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Attachment));
        Assert.That(Encoding.UTF8.GetString(entry.AttachmentRequestBody!), Is.EqualTo(body));
        Assert.That(entry.ResponseJson, Is.Null);
    }

    [Test]
    public async Task MultipartRecordsTheEnvelopeAndPartSizes()
    {
        var boundary = "scrytest";
        var bytes = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Type: application/octet-stream\r\n\r\nAB\r\n" +
            $"--{boundary}\r\nContent-Type: application/octet-stream\r\n\r\nABCD\r\n" +
            $"--{boundary}\r\nContent-Type: application/json\r\n\r\n{{\"kind\":\"List\"}}\r\n" +
            $"--{boundary}--\r\n");
        var (store, client) = Stubbed(
            _ =>
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/mixed; boundary={boundary}");
                return new(HttpStatusCode.OK) {Content = content};
            });

        using var body = JsonContent("""{"version":1,"root":"Person","pipeline":[]}""");
        using var response = await client.PostAsync("/api/query", body);

        // The caller still reads the exact multipart bytes.
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
        var entry = store.Entries.Single();
        Assert.That(entry.ResponseJson, Does.Contain("\"kind\": \"List\""));
        Assert.That(entry.BinaryPartSizes, Is.EqualTo([2, 4]));
    }

    [Test]
    public void ErrorResponseRecordsTheServersError()
    {
        var (store, client) = Stubbed(
            _ => new(HttpStatusCode.BadRequest)
            {
                Content = JsonContent(JsonSerializer.Serialize(new ScryError("nope"), ScryJson.Options))
            });

        Assert.ThrowsAsync<ScryRequestException>(
            () => Scry(client).Select(_ => new NameRow(_.Name)).ToListAsync());

        var entry = store.Entries.Single();
        Assert.That(entry.Status, Is.EqualTo(400));
        Assert.That(entry.Error, Is.EqualTo("nope"));
        Assert.That(entry.ResponseJson, Does.Contain("nope"));
    }

    [Test]
    public void TransportExceptionIsRecordedAndRethrown()
    {
        var (store, client) = Stubbed(_ => throw new HttpRequestException("unreachable"));

        Assert.ThrowsAsync<HttpRequestException>(
            () => Scry(client).Select(_ => new NameRow(_.Name)).ToListAsync());

        var entry = store.Entries.Single();
        Assert.That(entry.Error, Is.EqualTo("unreachable"));
        Assert.That(entry.Status, Is.Null);
    }

    [Test]
    public async Task OldestEntryIsEvictedBeyondMaxEntries()
    {
        var (store, client) = Stubbed(_ => List(), _ => _.MaxEntries = 2);

        for (var i = 0; i < 3; i++)
        {
            using var body = JsonContent($$"""{"version":1,"root":"Person","pipeline":[],"n":{{i}}}""");
            await client.PostAsync("/api/query", body);
        }

        Assert.That(store.Entries, Has.Count.EqualTo(2));
        Assert.That(store.Entries[0].RequestJson, Does.Contain("\"n\": 1"));
        Assert.That(store.Entries[1].RequestJson, Does.Contain("\"n\": 2"));
    }

    [Test]
    public async Task DisabledCapturesNothing()
    {
        var (store, client) = Stubbed(_ => List(), _ => _.Enabled = false);

        await Scry(client).Select(_ => new NameRow(_.Name)).ToListAsync();

        Assert.That(store.Entries, Is.Empty);
    }

    [Test]
    public async Task ChangedFiresOnAddAndClear()
    {
        var (store, client) = Stubbed(_ => List());
        var raised = 0;
        store.Changed += () => raised++;

        await Scry(client).Select(_ => new NameRow(_.Name)).ToListAsync();
        store.Clear();

        Assert.That(raised, Is.EqualTo(2));
        Assert.That(store.Entries, Is.Empty);
    }

    // The named client may carry the app's own calls beside Scry's; they are listed so the log is
    // honest about the wire, but nothing is decoded from them.
    [Test]
    public async Task UnrelatedTrafficIsKindOther()
    {
        var (store, client) = Stubbed(
            _ => new(HttpStatusCode.OK) {Content = new StringContent("pong")});

        await client.GetAsync("/api/ping");

        var entry = store.Entries.Single();
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Other));
        Assert.That(entry.Request, Is.Null);
        Assert.That(entry.Status, Is.EqualTo(200));
    }

    static IQueryable<PersonModel> Scry(HttpClient http) =>
        ScryClient.ForHttp(http, "/api/query").Source<PersonModel>("Person", ["Id", "Name"]);

    static (ScrySidecarStore Store, HttpClient Client) Stubbed(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<ScrySidecarOptions>? configure = null)
    {
        var options = new ScrySidecarOptions();
        configure?.Invoke(options);
        var store = new ScrySidecarStore(options);
        var handler = new ScrySidecarHandler(store, options)
        {
            InnerHandler = new StubHandler(respond)
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new("http://localhost")
        };

        return (store, client);
    }

    static HttpResponseMessage List() =>
        Json(ScryJson.Serialize(
            QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(Array.Empty<int>()))));

    static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent(json)
        };

    static ByteArrayContent JsonContent(string json)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new("application/json") {CharSet = "utf-8"};
        return content;
    }

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel) =>
            Task.FromResult(respond(request));
    }
}
