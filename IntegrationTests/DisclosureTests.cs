using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
using SampleContext = Sample.Model.SampleContext;

/// <summary>
/// The disclosure audit over real transports: that turning it on changes nothing about what is sent,
/// that an answer given over HTTP, the hub or MCP is recorded under whoever that transport says is
/// asking, that an answer the record will not take is not given, and that the SQL Server store
/// answers the questions the record exists for.
/// </summary>
/// <remarks>
/// What the audit records for each shape of query is asserted at the processor in <c>Scry.Tests</c>.
/// What is asserted here is what only a transport can get wrong: who the caller is, what the bytes
/// are once the response writer and the capture share a buffer, and what a client is left holding
/// when the record fails part-way through a response that had already started.
/// </remarks>
[NotInParallel]
[DependsOn<HttpRoundTripTests.StaleClient>(nameof(HttpRoundTripTests.StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
public partial class DisclosureTests
{
    static SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    const string names =
        """{"version":1,"root":"Employee","pipeline":[{"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},{"$type":"select","projection":{"members":["Name"]}}]}""";

    const string holidays =
        """{"version":1,"root":"Holiday","pipeline":[{"$type":"select","projection":{"members":["Name","Date"]}}]}""";

    // One database and two servers over it for the whole corpus, one with the audit on and one
    // without: every entry of it only reads.
    static SqlDatabase<SampleContext> shared = null!;
    static Server plain = null!;
    static Server audited = null!;

    [Before(Class)]
    public static async Task StartServers()
    {
        shared = await sqlInstance.Build();
        plain = await Server.Start(shared, audit: false);
        audited = await Server.Start(shared);
    }

    [After(Class)]
    public static async Task StopServers()
    {
        await audited.DisposeAsync();
        await plain.DisposeAsync();
        await shared.DisposeAsync();
    }

    // Turning the audit on adds key columns to what is read and takes every row through a second
    // buffer on its way out. Neither may show: for every shape in the fast writer's own corpus, the
    // bytes a client is sent are the bytes it was sent before — on the plan-cache miss and on the hit.
    [Test]
    [MethodDataSource(typeof(FastWriterGoldenTests), nameof(FastWriterGoldenTests.Corpus))]
    public async Task TheBytesSentAreTheSameWithTheAuditOn(string name, string request)
    {
        var before = (await audited.Store.Status()).Events;
        var expected = Unsealed(await plain.Post(request));
        using var miss = await audited.Send(request);
        var first = Unsealed(await miss.Content.ReadAsStringAsync());
        var second = Unsealed(await audited.Post(request));

        using (Assert.Multiple())
        {
            await Assert.That(first).IsEqualTo(expected).Because($"{name} (miss)");
            await Assert.That(second).IsEqualTo(expected).Because($"{name} (hit)");
            await Assert.That((await audited.Store.Status()).Events - before).IsEqualTo(2);

            // No cache may keep what was recorded as sent to one caller: a copy is read again with
            // no request, and a read with no request is one nothing could record.
            await Assert.That(miss.Headers.CacheControl!.NoStore).IsTrue();
            await Assert.That(miss.Headers.ETag).IsNull();
        }
    }

    // The same for a batch — every corpus request as an entry of one — and for a stream, whose rows
    // are held back a chunk at a time while the record of them is accepted.
    [Test]
    public async Task ABatchAndAStreamAreTheSameBytesToo()
    {
        await using var database = await sqlInstance.Build();
        await using var plain = await Server.Start(database, audit: false);
        await using var audited = await Server.Start(database);
        var batch = $$"""{"version":1,"queries":[{{string.Join(',', FastWriterGoldenTests.Corpus().Select(_ => _.Item2))}}]}""";

        var expected = Unsealed(await plain.Post(batch, "/api/query/batch"));
        var streamed = await plain.Post(holidays, "/api/query/stream");

        using (Assert.Multiple())
        {
            await Assert.That(Unsealed(await audited.Post(batch, "/api/query/batch"))).IsEqualTo(expected);
            await Assert.That(await audited.Post(holidays, "/api/query/stream")).IsEqualTo(streamed);
        }

        // One event for each entry of the batch, tied together, and one for the stream.
        var recorded = await audited.Store.ReceivedBy("tester", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        var entries = recorded.Where(_ => _.Event.Kind != ScryDisclosureKind.Stream).ToList();
        using (Assert.Multiple())
        {
            await Assert.That(entries).Count().IsEqualTo(FastWriterGoldenTests.Corpus().Count());
            await Assert.That(entries.Select(_ => _.Event.Correlation!.Split('/')[0]).Distinct()).Count().IsEqualTo(1);
            await Assert.That(recorded.Count(_ => _.Event.Kind == ScryDisclosureKind.Stream)).IsEqualTo(1);
        }
    }

    // Whoever a transport says is asking is who its answers are recorded under: the endpoint's
    // caller over HTTP, the connection's over the hub, the agent's over MCP. An attachment's fetch
    // and the schema an agent reads are answers too.
    [Test]
    public async Task EveryTransportRecordsUnderItsOwnCaller()
    {
        await using var database = await sqlInstance.Build();
        await using var server = await Server.Start(database);

        await server.Post(names, user: "http.user");
        using var handbook = await server.Send(
            ScryJson.Serialize(AttachmentRequest.Create("Department", "Handbook", [new("1", ClrTypeTag.Int32)])),
            "/api/query/attachment",
            "file.user");
        await using (var hub = await server.Hub("hub.user"))
        {
            await hub.InvokeAsync<string>("Query", names);
        }

        await using (var agent = await server.Agent("agent.user"))
        {
            await agent.CallToolAsync("describe_schema");
            await agent.CallToolAsync(
                "query",
                new Dictionary<string, object?>
                {
                    ["root"] = "Employee",
                    ["pipeline"] = JsonDocument.Parse("""[{"$type":"count"}]""").RootElement
                });
        }

        var seen = new List<string>();
        foreach (var user in new[] {"http.user", "file.user", "hub.user", "agent.user"})
        {
            var received = await server.Store.ReceivedBy(user, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
            received.Reverse();
            seen.Add($"{user}: {string.Join(", ", received.Select(_ => $"{_.Event.Kind} of '{_.Event.Source}' ({_.Close?.Outcome}, {_.Close?.Units})"))}");
        }

        var photo = (await server.Store.ReceivedBy("file.user", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).Single();
        var fetched = (await server.Store.Reconstruct(photo.Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(handbook.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(seen).IsEquivalentTo(
                [
                    "http.user: List of 'Employee' (Released, 4)",
                    "file.user: Attachment of 'Department' (Released, 1)",
                    "hub.user: List of 'Employee' (Released, 4)",
                    "agent.user: Schema of '' (Released, 1), Scalar of 'Employee' (Released, 1)"
                ],
                CollectionOrdering.Matching);

            // The handbook itself is recorded by its digest and its length, not kept: bytes are only
            // held where a host asks for them to be.
            await Assert.That(photo.Event.ContentType).IsEqualTo("text/plain");
            await Assert.That(fetched.Units.Single().Content.Held).IsFalse();
            await Assert.That(fetched.Units.Single().Content.Length).IsEqualTo("Engineering handbook.".Length);
            await Assert.That(fetched.Units.Single().Entities.Single().Key).IsEqualTo("[1]");
            await Assert.That(await server.Store.ReceiversOf("Employee", [1]).ToListAsync()).Count().IsEqualTo(2);
        }
    }

    // A caller nobody can name is not answered: the record exists to say who was sent what, and an
    // answer recorded against nobody is one it could never account for.
    [Test]
    public async Task AnAnswerForNobodyIsNotGiven()
    {
        await using var database = await sqlInstance.Build();
        await using var server = await Server.Start(database);

        using var response = await server.Send(names, user: null);
        var body = await response.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(body).DoesNotContain("Alice");
            await Assert.That((await server.Store.Status()).Events).IsEqualTo(0);
        }
    }

    // A record that cannot be written means an answer that is not given. Under the spill threshold
    // nothing has left, so it is an ordinary failure with a body that says so and carries no row.
    [Test]
    public async Task ARecordThatFailsBeforeAnythingLeftIsAnError()
    {
        var sink = new Failing(after: 0);
        await using var server = await Server.Start(database: null, sink: sink.Over, rows: () => Wide(3), threshold: 64 * 1024);

        using var response = await server.Send(holidays);
        var body = await response.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(body).Contains("Query execution failed.");
            await Assert.That(body).DoesNotContain("www");
            await Assert.That((await server.Store.Status()).Events).IsEqualTo(0);
        }
    }

    // Past the threshold a response is sent a part at a time, and each part only once the record
    // holds the rows in it. The record failing after the first part cuts the response short: the
    // client is left without the end of the answer, and the record holds every row it was sent —
    // and may hold more, which is the direction that never under-reports.
    [Test]
    public async Task ARecordThatFailsPartWayCutsTheAnswerShort()
    {
        var sink = new Failing(after: 1);
        await using var server = await Server.Start(database: null, sink: sink.Over, rows: () => Wide(600), threshold: 64 * 1024);

        using var response = await server.Send(holidays, completion: HttpCompletionOption.ResponseHeadersRead);
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception)
        {
            // The other way a truncation presents: the body ended before the host said it would.
        }

        var recorded = (await server.Store.ReceivedBy("tester", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).Single();
        var units = (await server.Store.Reconstruct(recorded.Event.Id))!.Units.Count;
        var sent = 0;
        if (body is not null)
        {
            sent = WideRow().Count(body);
        }

        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(body is null || !body.Contains("\"stamp\"")).IsTrue().Because("a truncated response must not look complete");
            await Assert.That(units).IsGreaterThan(0);
            await Assert.That(units).IsLessThan(600);
            await Assert.That(sent).IsLessThanOrEqualTo(units).Because("no row may reach a client that the record does not hold");

            // The close could not be written either, so the event has none, and is read as one that
            // may have been sent.
            await Assert.That(recorded.Close).IsNull();
        }
    }

    // The whole of it through SQL Server: answers recorded into the application's own database as
    // they are sent, moved on, and the three questions asked of what is there.
    [Test]
    public async Task TheSqlStoreAnswersWhatWasAskedOverHttp()
    {
        const string passwords =
            """{"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"binary","op":"Equal","left":{"$type":"member","path":["Department","Name"]},"right":{"$type":"const","value":"Engineering","tag":"String"}}},{"$type":"select","projection":{"members":["Name","Password",{"name":"Department","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}}]}""";
        await using var database = await sqlInstance.Build();
        await using var server = await Server.Start(database, sql: true);
        var store = server.Service<ScrySqlServerDisclosureStore>();

        var sent = await server.Post(names, user: "alice");
        await server.Post(passwords, user: "alice");
        await server.Post(names, user: "bob");
        await store.DrainAsync();

        var received = await store.ReceiversOf("Employee", [1]).ToListAsync();
        var alice = await store.ReceivedBy("alice", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
        var rebuilt = (await store.Reconstruct(alice[^1].Event.Id))!;
        var rows = string.Join(',', rebuilt.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)));
        using (Assert.Multiple())
        {
            // Who received a row, newest first, and which version: the two callers sent only a name
            // were sent the same thing.
            await Assert.That(received.Select(_ => _.Event.Caller!)).IsEquivalentTo(["bob", "alice", "alice"], CollectionOrdering.Matching);
            await Assert.That(received[0].Content).IsEqualTo(received[2].Content);
            await Assert.That(received[1].Content).IsNotEqualTo(received[2].Content);

            // What a caller received, with the response put back together from the store.
            await Assert.That(alice).Count().IsEqualTo(2);
            await Assert.That(sent).Contains($"\"payload\":[{rows}]");
            await Assert.That(alice[0].Event.Sensitive).IsTrue();

            // Whether a caller ever received a member.
            await Assert.That(await store.MemberReceivedBy("alice", "Employee", "Password").ToListAsync()).Count().IsEqualTo(1);
            await Assert.That(await store.MemberReceivedBy("bob", "Employee", "Password").ToListAsync()).IsEmpty();
            await Assert.That(await store.ReceiversOf("Department", [1]).ToListAsync()).Count().IsEqualTo(2);
            await Assert.That((await store.Status()).Pending).IsEqualTo(0);
        }
    }

    // A page's cursor is sealed under a fresh nonce every time it is minted, so two renderings of one
    // page agree on every byte but that value. What is compared is everything around it.
    static string Unsealed(string body) =>
        CursorValue().Replace(body, "$1\"{cursor}\"");

    [GeneratedRegex("(\"cursor\":)\"[^\"]*\"")]
    private static partial Regex CursorValue();

    [GeneratedRegex("\"name\":\"w{200}\"")]
    private static partial Regex WideRow();

    // Wide rows, so a few hundred of them clear the spill threshold, and distinctive ones, so that
    // how many of them reached a client can be counted.
    static IEnumerable<Sample.Model.Holiday> Wide(int count) =>
        Enumerable
            .Range(0, count)
            .Select(index => new Sample.Model.Holiday
            {
                Name = new('w', 200),
                Date = new DateOnly(2030, 1, 1).AddDays(index)
            });

    // A record that takes so many batches and then no more.
    sealed class Failing(int after)
    {
        int taken;

        int After => after;

        public IScryDisclosureSink Over(ScryMemoryDisclosureStore store) =>
            new Sink(this, store);

        sealed class Sink(Failing owner, ScryMemoryDisclosureStore store) :
            IScryDisclosureSink
        {
            public void Append(ScryDisclosureBatch batch)
            {
                if (Interlocked.Increment(ref owner.taken) > owner.After)
                {
                    throw new IOException("The record is not there.");
                }

                store.Append(batch);
            }

            public ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
            {
                Append(batch);
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A server over the sample model with every transport mapped, the audit on unless told
    /// otherwise, and a caller who is whoever the <c>X-User</c> header says: a stand-in for a
    /// sign-in, which is all a test needs of one.
    /// </summary>
    sealed class Server(WebApplication app, ScryMemoryDisclosureStore store) :
        IAsyncDisposable
    {
        public ScryMemoryDisclosureStore Store => store;

        public T Service<T>()
            where T : notnull =>
            app.Services.GetRequiredService<T>();

        public static async Task<Server> Start(
            SqlDatabase<SampleContext>? database,
            bool audit = true,
            bool sql = false,
            Func<ScryMemoryDisclosureStore, IScryDisclosureSink>? sink = null,
            Func<IEnumerable<Sample.Model.Holiday>>? rows = null,
            int? threshold = null,
            Action<ScryOptions>? extra = null)
        {
            var store = new ScryMemoryDisclosureStore();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var services = builder.Services;
            services.AddSignalR();

            // A server whose every row comes from memory never opens a connection, so it is given
            // one that goes nowhere.
            var connection = database?.ConnectionString ?? "Server=(localdb)\\ScryUnused;Database=ScryUnused";
            services.AddDbContext<SampleContext>(_ => _.UseSqlServer(connection));
            services.AddScry<SampleContext>(options =>
            {
                options.AddPocoSource(_ => rows?.Invoke() ?? Sample.Model.Holiday.Seed());
                options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
                options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
                options.MaxPageSize = 200;
                options.Mcp = ScryMcpAccess.Read;
                options.Caller = Named;
                if (threshold is { } spill)
                {
                    options.ResponseSpillThreshold = spill;
                }

                if (!audit)
                {
                    return;
                }

                if (sql)
                {
                    // Moved on when the test says so, so that what it then reads is what it expects.
                    options.UseSqlServerDisclosureAudit(connection, _ => _.DrainInterval = null, Rows);
                    return;
                }

                options.UseDisclosureAudit(sink?.Invoke(store) ?? store, Rows);
                extra?.Invoke(options);
            });
            services.AddScryMcp();

            var app = builder.Build();
            try
            {
                app.MapScry("/api/query");
                app.MapScryHub("/hub");
                app.MapScryMcp("/mcp");
                await app.StartAsync();
                return new(app, store);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        // What identifies a row of the two sources the model gives no key: the view by what its rows
        // are grouped on, and the list from memory by nothing at all.
        static void Rows(ScryDisclosureOptions audit)
        {
            audit.Key<Sample.Model.EmployeeSummary>(_ => _.Department);
            audit.Unkeyed<Sample.Model.Holiday>();
        }

        static string? Named(HttpContext context)
        {
            if (context.Request.Headers.TryGetValue("X-User", out var user))
            {
                return user.ToString();
            }

            return null;
        }

        // Asks by URL, as a client that may be answered from what it holds does: with the tag of the
        // copy it kept, where it has one.
        public async Task<HttpResponseMessage> Get(string request, string? tag = null, string? user = "tester")
        {
            var client = app.GetTestClient();
            var encoded = QueryUrl.Encode(ScryJson.DeserializeRequest(Encoding.UTF8.GetBytes(request)));
            using var message = new HttpRequestMessage(HttpMethod.Get, $"/api/query?{QueryUrl.Parameter}={encoded}");
            if (tag is not null)
            {
                message.Headers.TryAddWithoutValidation("If-None-Match", tag);
            }

            if (user is not null)
            {
                message.Headers.Add("X-User", user);
            }

            return await client.SendAsync(message);
        }

        public async Task<HttpResponseMessage> Send(
            string request,
            string path = "/api/query",
            string? user = "tester",
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        {
            var client = app.GetTestClient();
            using var message = new HttpRequestMessage(HttpMethod.Post, path);
            message.Content = new StringContent(request, Encoding.UTF8, "application/json");
            if (user is not null)
            {
                message.Headers.Add("X-User", user);
            }

            return await client.SendAsync(message, completion);
        }

        public async Task<string> Post(string request, string path = "/api/query", string? user = "tester")
        {
            using var response = await Send(request, path, user);
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
            return body;
        }

        // A hub connection whose every request says who it is from.
        public async Task<HubConnection> Hub(string user)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(
                    "http://localhost/hub",
                    options =>
                    {
                        options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                        options.Transports = HttpTransportType.LongPolling;
                        options.Headers["X-User"] = user;
                    })
                .Build();
            await connection.StartAsync();
            return connection;
        }

        // An agent connected as the named user.
        public Task<McpClient> Agent(string user)
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-User", user);
            var transport = new HttpClientTransport(
                new()
                {
                    Endpoint = new("http://localhost/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                client,
                ownsHttpClient: true);
            return McpClient.CreateAsync(transport);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
