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

    // A live query's response never ends, so buffering it would hold the first answer back for ever.
    // It is watched as it flows instead, which must be indistinguishable from not watching it: the
    // response is handed back before a single byte has arrived.
    [Test]
    public async Task ALiveQueryIsNeverBuffered()
    {
        var served = ChunkStream.Shut(Ping());
        var (store, client) = Stubbed(_ => Sse(served));

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.That(served.Reads, Is.Empty);
        var entry = store.Entries.Single();
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Subscription));
        Assert.That(entry.Request?.Root, Is.EqualTo("Person"));
        Assert.That(entry.ResponseJson, Is.Null);
        Assert.That(entry.Session, Is.Not.Null);
        Assert.That(entry.Session!.Connections.Single().Status, Is.EqualTo(200));
    }

    // Watching may not change what the consumer reads — not the bytes, and not the boundaries they
    // arrive on, since a reader parsing a stream sees both.
    [Test]
    public async Task ALiveQueryKeepsItsBytesAndItsBoundaries()
    {
        string[] chunks = ["event: re", "sult\nid: a3f1\ndata: {\"a\":1}\n", "\nevent: ping\ndata: \n\n"];
        var served = new ChunkStream(chunks);
        var (_, client) = Stubbed(_ => Sse(served));

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var (text, boundaries) = await Drain(response);

        Assert.That(text, Is.EqualTo(string.Concat(chunks)));
        Assert.That(boundaries, Is.EqualTo(chunks.Select(_ => _.Length)).AsCollection);
    }

    // The client refuses a live query whose response is not an event stream, so a header lost in the
    // hand-back would present as "this server is not serving live queries".
    [Test]
    public async Task ALiveQueryKeepsItsContentType()
    {
        var (_, client) = Stubbed(_ => Sse(new ChunkStream(Ping())));

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(ScryLive.ContentType));
    }

    // Every event, the heartbeats included: an idle live query that is still being pinged looks
    // exactly like a hung one without them.
    [Test]
    public async Task ALiveQueryRecordsWhatCameBack()
    {
        var (store, client) = Stubbed(
            _ => Sse(new ChunkStream(Result("a3f1"), Ping(), Unchanged(), End("lifetime", reconnect: false))));

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await Drain(response);

        var session = store.Entries.Single().Session!;
        var connection = session.Connections.Single();
        Assert.That(connection.Events.Select(_ => _.Name), Is.EqualTo([ScryLive.Result, ScryLive.Ping, ScryLive.Unchanged, ScryLive.End]).AsCollection);
        Assert.That(connection.Events[0].EventId, Is.EqualTo("a3f1"));
        Assert.That(connection.Events[0].Json, Does.Contain("\"kind\""));
        Assert.That(connection.Ended, Is.EqualTo("lifetime"));
        Assert.That(session.Answers, Is.EqualTo(1));
        Assert.That(session.Pings, Is.EqualTo(1));
        Assert.That(session.Unchanged, Is.EqualTo(1));
        Assert.That(session.State, Is.EqualTo(ScrySubscriptionState.Closed));
    }

    // Reads fall wherever the network puts them, and an event is not obliged to arrive in one.
    [Test]
    public async Task AnEventSplitAcrossReadsIsStillOneEvent()
    {
        var whole = Result("a3f1");
        var (store, client) = Stubbed(_ => Sse(new ChunkStream(whole[..9], whole[9..20], whole[20..])));

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await Drain(response);

        var connection = store.Entries.Single().Session!.Connections.Single();
        Assert.That(connection.Events.Single().Name, Is.EqualTo(ScryLive.Result));
        Assert.That(connection.Events.Single().EventId, Is.EqualTo("a3f1"));
        Assert.That(connection.Events.Single().Bytes, Is.EqualTo(whole.Length));
    }

    // Whatever ended the read is the consumer's to classify, so it is recorded and rethrown exactly
    // as it was — watching the stream may not change what reading it does.
    [Test]
    public async Task AReadFailureIsRecordedAndReachesTheCaller()
    {
        var served = ChunkStream.Failing(new IOException("The connection was reset."), Result("a3f1"));
        var (store, client) = Stubbed(_ => Sse(served));

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        var failure = Assert.ThrowsAsync<IOException>(async () => await Drain(response));

        Assert.That(failure!.Message, Is.EqualTo("The connection was reset."));
        var session = store.Entries.Single().Session!;
        Assert.That(session.Connections.Single().Ended, Is.EqualTo("cut"));
        Assert.That(session.Error, Is.EqualTo("The connection was reset."));
    }

    // A stream that stops without a closing event was cut rather than ended. The client asks again,
    // and the panel says which of the two happened.
    [Test]
    public async Task ALiveQueryThatStopsWithoutSayingSoIsCut()
    {
        var (store, client) = Stubbed(_ => Sse(new ChunkStream(Result("a3f1"))));

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await Drain(response);

        var session = store.Entries.Single().Session!;
        Assert.That(session.Connections.Single().Ended, Is.EqualTo("cut"));
        Assert.That(session.State, Is.EqualTo(ScrySubscriptionState.Reconnecting));
    }

    // The client names the live query a connection belongs to beside the request, so a reconnect
    // lands on the row it resumed rather than starting one.
    [Test]
    public async Task AReconnectFoldsIntoTheRowItResumed()
    {
        var (store, client) = Stubbed(_ => Sse(new ChunkStream(Result("a3f1"))));

        await Connect(client, session: 7, resumedFrom: null);
        await Connect(client, session: 7, resumedFrom: "a3f1");

        var entry = store.Entries.Single();
        Assert.That(entry.Session!.Connections.Select(_ => _.Attempt), Is.EqualTo([1, 2]).AsCollection);
        Assert.That(entry.Session.Connections[1].ResumedFrom, Is.EqualTo("a3f1"));
        Assert.That(entry.Session.Answers, Is.EqualTo(2));
    }

    // Two live queries asking the same thing are two live queries, however alike their traffic.
    [Test]
    public async Task TwoLiveQueriesAreTwoRows()
    {
        var (store, client) = Stubbed(_ => Sse(new ChunkStream(Result("a3f1"))));

        await Connect(client, session: 7, resumedFrom: null);
        await Connect(client, session: 8, resumedFrom: null);

        Assert.That(store.Entries, Has.Count.EqualTo(2));
    }

    // The cap counts exchanges that are over. A live query still open is the one thing in the log
    // still being written to, so it is not what makes room.
    [Test]
    public async Task AnOpenLiveQueryIsNotEvicted()
    {
        var (store, client) = Stubbed(
            _ => _.RequestUri!.AbsolutePath.EndsWith("subscribe", StringComparison.Ordinal)
                ? Sse(new ChunkStream(Ping()))
                : List(),
            _ => _.MaxEntries = 2);

        using var request = Subscribe();
        using var opened = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        for (var i = 0; i < 5; i++)
        {
            await Scry(client).Select(_ => new NameRow(_.Name)).ToListAsync();
        }

        Assert.That(store.Entries.Count(_ => _.Session is not null), Is.EqualTo(1));
        Assert.That(store.Entries, Has.Count.EqualTo(2));
    }

    // A refused live query never becomes a stream, so there is nothing to watch and everything to
    // read: it is buffered like any other failure, and the server's reason is shown.
    [Test]
    public async Task ARefusedLiveQueryShowsTheServersReason()
    {
        var (store, client) = Stubbed(
            _ => new(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent(ScryJson.Serialize(new ScryError("Too many live queries.") {Code = ScryErrorCode.SubscriptionLimit}))
            });

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        var entry = store.Entries.Single();
        Assert.That(entry.Kind, Is.EqualTo(ScrySidecarKind.Subscription));
        Assert.That(entry.Session, Is.Null);
        Assert.That(entry.Status, Is.EqualTo(503));
        Assert.That(entry.Error, Is.EqualTo("Too many live queries."));

        // Still readable above the handler, which is what the client does with a refusal.
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("Too many live queries."));
    }

    // A live query answers for as long as it is open, which is far oftener than a panel can usefully
    // repaint. The two signals are kept apart so a subscriber mirroring the log is not woken by every
    // heartbeat.
    [Test]
    public async Task EventsRaiseSessionChangedAndNotChanged()
    {
        var (store, client) = Stubbed(_ => Sse(new ChunkStream(Result("a3f1"), Ping(), Ping())));
        var changed = 0;
        var sessions = 0;
        store.Changed += () => changed++;
        store.SessionChanged += () => sessions++;

        using var request = Subscribe();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await Drain(response);

        Assert.That(changed, Is.EqualTo(1));
        Assert.That(sessions, Is.GreaterThan(3));
    }

    static async Task Connect(HttpClient client, long session, string? resumedFrom)
    {
        using var request = Subscribe();
        LiveSessionStamp.Write(request, session);
        if (resumedFrom is not null)
        {
            request.Headers.TryAddWithoutValidation(ScryLive.LastEventIdHeader, resumedFrom);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await Drain(response);
    }

    static HttpRequestMessage Subscribe() =>
        new(HttpMethod.Post, "/api/query/subscribe")
        {
            Content = JsonContent(ScryJson.Serialize(QueryRequest.Create("Person", [new CountOp()])))
        };

    static HttpResponseMessage Sse(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new(ScryLive.ContentType);
        return new(HttpStatusCode.OK) {Content = content};
    }

    // Reads the response the way a live query's consumer does, and reports what it saw: the bytes,
    // and the boundaries they came on.
    static async Task<(string Text, List<int> Boundaries)> Drain(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        var text = new StringBuilder();
        var boundaries = new List<int>();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return (text.ToString(), boundaries);
            }

            boundaries.Add(read);
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    static string Result(string id) =>
        $"event: result\nid: {id}\ndata: {ScryJson.Serialize(QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(Array.Empty<int>())))}\n\n";

    static string Ping() =>
        "event: ping\ndata: \n\n";

    static string Unchanged() =>
        "event: unchanged\ndata: \n\n";

    static string End(string reason, bool reconnect) =>
        $"event: end\ndata: {Encoding.UTF8.GetString(ScryJson.SerializeToUtf8(new ScryLiveEnd(reconnect) {Reason = reason}))}\n\n";

    static byte[] Utf8(string text) =>
        Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// A response body handed over one read per chunk, at exactly the boundaries the test asked for.
    /// Shut until <see cref="Release"/>, so a test can prove the response was handed back before any
    /// of it had arrived.
    /// </summary>
    sealed class ChunkStream :
        Stream
    {
        Queue<byte[]> chunks;
        Exception? ending;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ChunkStream(IEnumerable<string> chunks, bool shut, Exception? ending)
        {
            this.chunks = new(chunks.Select(Encoding.UTF8.GetBytes));
            this.ending = ending;
            if (!shut)
            {
                Release();
            }
        }

        public ChunkStream(params string[] chunks) :
            this(chunks, shut: false, ending: null)
        {
        }

        /// <summary>The same, held shut until <see cref="Release"/>.</summary>
        public static ChunkStream Shut(params string[] chunks) =>
            new(chunks, shut: true, ending: null);

        /// <summary>The same, failing rather than ending once the chunks have run out.</summary>
        public static ChunkStream Failing(Exception ending, params string[] chunks) =>
            new(chunks, shut: false, ending);

        /// <summary>The size of every read that was answered, in order.</summary>
        public List<int> Reads { get; } = [];

        public void Release() =>
            gate.TrySetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, Cancel cancel = default)
        {
            await gate.Task.WaitAsync(cancel);
            if (chunks.Count == 0)
            {
                return ending is null ? 0 : throw ending;
            }

            var chunk = chunks.Dequeue();
            chunk.CopyTo(buffer);
            Reads.Add(chunk.Length);
            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
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
            // Told apart by the stamp, a member the vocabulary names: one it does not is refused.
            using var body = JsonContent($$"""{"version":1,"root":"Person","pipeline":[],"stamp":"s{{i}}"}""");
            await client.PostAsync("/api/query", body);
        }

        Assert.That(store.Entries, Has.Count.EqualTo(2));
        Assert.That(store.Entries[0].RequestJson, Does.Contain("\"stamp\": \"s1\""));
        Assert.That(store.Entries[1].RequestJson, Does.Contain("\"stamp\": \"s2\""));
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
