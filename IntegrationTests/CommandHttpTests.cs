using System.Linq.Expressions;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// Commands over HTTP, read as raw bodies and with the platform's own server-sent-events parser, so
/// that what is pinned is the wire: which answers are statuses, what a receipt says, how a pending
/// command's stream runs and ends, and that a row a caller may not act on answers exactly as a row
/// that is not there. Self-contained, with a model, context and server of its own.
/// </summary>
[TestFixture]
public class CommandHttpTests
{
    const string userHeader = HeaderUserHandler.Header;

    SqlDatabase<LedgerContext> database = null!;

    [OneTimeSetUp]
    public async Task BuildDatabase() =>
        database = await LedgerData.Instance.Build();

    [OneTimeTearDown]
    public async Task DropDatabase() =>
        await database.DisposeAsync();

    [Test]
    public async Task ACommandFinishingInsideTheWindowIsAnsweredWithItsReceipt()
    {
        await using var server = await Server.Start(database);

        using var response = await server.Post(Command("OpenLedger", new {name = "Petty cash"}));

        var body = await response.Content.ReadAsStringAsync();
        var receipt = ScryJson.DeserializeReceipt(body);
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
            Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
            Assert.That(response.Headers.GetValues(WireFormat.SchemaStampHeader).Single(), Is.Not.Empty);
            Assert.That(receipt.Status, Is.EqualTo(CommandStatus.Completed));
            Assert.That(receipt.Result!.Value.GetProperty("id").GetInt32(), Is.GreaterThan(3));
        });
    }

    [Test]
    public async Task ASlowCommandIsAStreamOfItsReceipts()
    {
        await using var server = await Server.Start(database, _ => _.CommandSyncWindow = TimeSpan.Zero);
        var gate = server.Gate();

        await using var stream = await server.Stream(Command("RenameLedger", new {id = 1, name = "Cash"}));
        var pending = await stream.Next();
        gate.SetResult();
        var final = await stream.Next();

        Assert.Multiple(() =>
        {
            Assert.That(stream.Response.Content.Headers.ContentType!.MediaType, Is.EqualTo(ScryLive.ContentType));
            Assert.That(stream.Response.Headers.CacheControl!.NoStore, Is.True);
            Assert.That(pending.EventType, Is.EqualTo(ScryLive.Result));
            Assert.That(ScryJson.DeserializeReceipt(pending.Data).Status, Is.EqualTo(CommandStatus.Pending));
            Assert.That(ScryJson.DeserializeReceipt(final.Data).Status, Is.EqualTo(CommandStatus.Completed));
        });
        Assert.That(await stream.Ended(), Is.True);
    }

    // Asked again by its id — after a cut, or an end the server chose — a command in flight answers with
    // the same stream, and one that has finished with its receipt.
    [Test]
    public async Task ACommandIsAskedForAgainByItsId()
    {
        await using var server = await Server.Start(database, _ => _.CommandSyncWindow = TimeSpan.Zero);
        var gate = server.Gate();
        var id = Guid.NewGuid();
        await using (var first = await server.Stream(Command("RenameLedger", new {id = 1, name = "Cash"}, id)))
        {
            Assert.That(ScryJson.DeserializeReceipt((await first.Next()).Data).Status, Is.EqualTo(CommandStatus.Pending));
        }

        await using var again = await server.StreamReceipt(id);
        Assert.That(ScryJson.DeserializeReceipt((await again.Next()).Data).Status, Is.EqualTo(CommandStatus.Pending));

        gate.SetResult();
        Assert.That(ScryJson.DeserializeReceipt((await again.Next()).Data).Status, Is.EqualTo(CommandStatus.Completed));

        using var finished = await server.Http.GetAsync($"/api/query/command/{id:D}");
        Assert.Multiple(async () =>
        {
            Assert.That(finished.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(ScryJson.DeserializeReceipt(await finished.Content.ReadAsStringAsync()).Status, Is.EqualTo(CommandStatus.Completed));
        });
    }

    // Another caller asking for a command by its id is told exactly what an id nobody sent is told.
    [Test]
    public async Task AReceiptIsOnlyForItsCaller()
    {
        await using var server = await Server.Start(database, authenticate: true);
        var id = Guid.NewGuid();
        using (var sent = await server.Post(Command("OpenLedger", new {name = "Alice's"}, id), "alice"))
        {
            Assert.That(sent.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        using var stranger = await server.Get($"/api/query/command/{id:D}", "bob");
        using var unknown = await server.Get($"/api/query/command/{Guid.NewGuid():D}", "bob");
        using var own = await server.Get($"/api/query/command/{id:D}", "alice");

        Assert.Multiple(async () =>
        {
            Assert.That(stranger.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await stranger.Content.ReadAsStringAsync(), Is.EqualTo(await unknown.Content.ReadAsStringAsync()));
            Assert.That(own.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    // A stream ended for the server's own reason says so, and asking again is a new request.
    [Test]
    public async Task APendingStreamEndsAtItsLifetimeAndIsAskedForAgain()
    {
        await using var server = await Server.Start(
            database,
            _ =>
            {
                _.CommandSyncWindow = TimeSpan.Zero;
                _.SubscriptionLifetime = TimeSpan.FromMilliseconds(300);
            });
        var gate = server.Gate();
        var id = Guid.NewGuid();

        await using var stream = await server.Stream(Command("RenameLedger", new {id = 2, name = "Bank"}, id));
        await stream.Next();
        var end = await stream.Next();
        Assert.That(end.EventType, Is.EqualTo(ScryLive.End));
        Assert.That(ScryJson.DeserializeLiveEnd(System.Text.Encoding.UTF8.GetBytes(end.Data)).Reconnect, Is.True);

        gate.SetResult();
        await using var again = await server.StreamReceipt(id);
        var receipt = ScryJson.DeserializeReceipt((await again.Next()).Data);
        if (receipt.Status == CommandStatus.Pending)
        {
            receipt = ScryJson.DeserializeReceipt((await again.Next()).Data);
        }

        Assert.That(receipt.Status, Is.EqualTo(CommandStatus.Completed));
    }

    [TestCase("""{ not json""", ScryErrorCode.WireFormat, "Invalid query command request")]
    [TestCase("""{"version":1,"command":"Teleport","id":"a3f1c0de-0000-4000-8000-000000000001","payload":{}}""", ScryErrorCode.Validation, "Unknown command 'Teleport'.")]
    [TestCase("""{"version":1,"command":"RenameLedger","id":"a3f1c0de-0000-4000-8000-000000000001","payload":{"id":1,"name":"x","renamedBy":"m"}}""", ScryErrorCode.Validation, "carries 'renamedBy', which the command does not have.")]
    [TestCase("""{"version":1,"command":"RenameLedger","id":"a3f1c0de-0000-4000-8000-000000000001","payload":{"name":"x"}}""", ScryErrorCode.Validation, "is missing 'id'.")]
    [TestCase("""{"version":2,"command":"RenameLedger","id":"a3f1c0de-0000-4000-8000-000000000001","payload":{"id":1,"name":"x"}}""", ScryErrorCode.Validation, "Unsupported command request version 2")]
    public async Task ARequestThatCannotBeHandledIsA400(string body, ScryErrorCode code, string message)
    {
        await using var server = await Server.Start(database);

        using var response = await server.PostRaw(body);

        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsStringAsync())!;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error.Code, Is.EqualTo(code));
            Assert.That(error.Error, Does.Contain(message));
            Assert.That(response.Headers.GetValues(WireFormat.SchemaStampHeader).Single(), Is.Not.Empty);
        });
    }

    [Test]
    public async Task ADriftedClientIsToldItIsStale()
    {
        await using var server = await Server.Start(database);

        using var response = await server.PostRaw("""{"version":1,"command":"Teleport","id":"a3f1c0de-0000-4000-8000-000000000001","payload":{},"stamp":"not-this-server"}""");

        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsStringAsync())!;
        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo(ScryErrorCode.StaleClient));
            Assert.That(error.Error, Does.Contain("regenerate the client"));
        });
    }

    [Test]
    public async Task ABodyOverTheLimitIsRefusedUnread()
    {
        await using var server = await Server.Start(database, _ => _.MaxCommandBytes = 64);

        using var response = await server.Post(Command("OpenLedger", new {name = new string('x', 200)}));

        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsStringAsync())!;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(error.Code, Is.EqualTo(ScryErrorCode.PayloadTooLarge));
        });
    }

    [Test]
    public async Task ABodyThatIsNotJsonIsRefused()
    {
        await using var server = await Server.Start(database);

        using var content = new StringContent(ScryJson.Serialize(Command("OpenLedger", new {name = "x"})), System.Text.Encoding.UTF8, "text/plain");
        using var response = await server.Http.PostAsync("/api/query/command", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    [Test]
    public async Task ACallerThePolicyRefusesOutrightIsA403()
    {
        await using var server = await Server.Start(database, authenticate: true);

        using var response = await server.Post(Command("RenameLedger", new {id = 1, name = "Mine"}), "mallory");

        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsStringAsync())!;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(error.Error, Is.EqualTo(ScryPermissionException.CommandDeniedMessage));
            Assert.That(error.Code, Is.EqualTo(ScryErrorCode.Forbidden));
        });
    }

    // A row the command's policy denies and a row that is not there are one answer, byte for byte.
    [Test]
    public async Task ADeniedRowAndAMissingRowAreTheSame404()
    {
        await using var server = await Server.Start(database);

        using var denied = await server.Post(Command("RenameLedger", new {id = 3, name = "Unlocked"}));
        using var missing = await server.Post(Command("RenameLedger", new {id = 999, name = "Found"}));

        Assert.Multiple(async () =>
        {
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await denied.Content.ReadAsStringAsync(), Is.EqualTo(await missing.Content.ReadAsStringAsync()));
        });
    }

    [Test]
    public async Task OneCommandTooManyIsA503AndOneTooManyForTheCallerA429()
    {
        await using var server = await Server.Start(
            database,
            _ =>
            {
                _.CommandSyncWindow = TimeSpan.Zero;
                _.MaxPendingCommands = 2;
                _.MaxPendingCommandsPerCaller = 1;
            },
            authenticate: true);
        var gate = server.Gate();
        await using var alice = await server.Stream(Command("RenameLedger", new {id = 1, name = "Cash"}), "alice");
        await alice.Next();

        using var aliceAgain = await server.Post(Command("RenameLedger", new {id = 1, name = "Cash"}), "alice");
        await using var bob = await server.Stream(Command("RenameLedger", new {id = 2, name = "Bank"}), "bob");
        await bob.Next();
        using var carol = await server.Post(Command("RenameLedger", new {id = 2, name = "Bank"}), "carol");
        gate.SetResult();

        Assert.Multiple(async () =>
        {
            Assert.That(aliceAgain.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(aliceAgain.Headers.RetryAfter, Is.Not.Null);
            Assert.That(carol.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(ScryJson.TryDeserializeError(await carol.Content.ReadAsStringAsync())!.Code, Is.EqualTo(ScryErrorCode.CommandLimit));
        });
    }

    // A server that has not said how many commands it will hold maps no command route at all.
    [Test]
    public async Task CommandsOffMapNothing()
    {
        await using var server = await Server.Start(database, _ => _.MaxPendingCommands = 0);

        using var command = await server.Post(Command("OpenLedger", new {name = "x"}));
        using var capabilities = await server.Http.GetAsync("/api/query/capabilities");

        Assert.Multiple(() =>
        {
            Assert.That(command.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(capabilities.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task CapabilitiesAreTheCallersOwn()
    {
        await using var server = await Server.Start(database, authenticate: true);

        using var alice = await server.Get("/api/query/capabilities", "alice");
        using var mallory = await server.Get("/api/query/capabilities", "mallory");

        Assert.Multiple(async () =>
        {
            Assert.That(alice.Headers.CacheControl!.NoStore, Is.True);
            Assert.That(ScryJson.DeserializeCapabilities(await alice.Content.ReadAsStringAsync()).Commands, Is.EqualTo(["OpenLedger", "RenameLedger"]));
            Assert.That(ScryJson.DeserializeCapabilities(await mallory.Content.ReadAsStringAsync()).Commands, Is.EqualTo(["OpenLedger"]));
        });
    }

    // The per-row capability is the command's policy decided in the query, per caller.
    [Test]
    public async Task TheCapabilityMemberIsTheCallersOwn()
    {
        await using var server = await Server.Start(database, authenticate: true);

        var alice = await server.Capabilities("alice");
        var mallory = await server.Capabilities("mallory");

        Assert.Multiple(() =>
        {
            Assert.That(alice, Is.EqualTo(new[] {(1, true), (2, true), (3, false)}));
            Assert.That(mallory, Is.EqualTo(new[] {(1, false), (2, false), (3, false)}));
        });
    }

    // Whatever guards the endpoint guards every command route, as it guards every query route.
    [Test]
    public async Task AuthorizationOnTheEndpointReachesEveryCommandRoute()
    {
        await using var server = await Server.Start(database, authenticate: true, requireWriter: true);

        using var command = await server.Post(Command("OpenLedger", new {name = "x"}), "alice");
        using var receipt = await server.Get($"/api/query/command/{Guid.NewGuid():D}", "alice");
        using var capabilities = await server.Get("/api/query/capabilities", "alice");

        Assert.Multiple(() =>
        {
            Assert.That(command.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(receipt.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(capabilities.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    // What a command wrote reaches a live query on the same server, through the interceptor on the
    // handler's context — nothing about the command is sent to it.
    [Test]
    public async Task ALiveQuerySeesTheCommandsWrite()
    {
        await using var server = await Server.Start(database);
        var request = QueryRequest.Create(
            "Ledger",
            [
                new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Id"]), new ConstNode("2", ClrTypeTag.Int32))),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);
        var subscribe = new HttpRequestMessage(HttpMethod.Post, "/api/query/subscribe")
        {
            Content = new ByteArrayContent(ScryJson.SerializeToUtf8(request))
            {
                Headers = {ContentType = new("application/json")}
            }
        };
        using var response = await server.Http.SendAsync(subscribe, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        await using var events = SseParser.Create(body).EnumerateAsync().GetAsyncEnumerator();
        Assert.That(await Next(events), Does.Contain("\"Bank\""));

        using (var renamed = await server.Post(Command("RenameLedger", new {id = 2, name = "Savings"})))
        {
            Assert.That(renamed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        Assert.That(await Next(events), Does.Contain("\"Savings\""));
    }

    // From here down the far side is the client rather than a parser: a command class in, an outcome
    // out, over the real endpoints.
    [Test]
    public async Task TheClientSendsACommandAndReadsItsResult()
    {
        await using var server = await Server.Start(database);
        var client = server.Client();

        var outcome = await client.SendCommandAsync<OpenLedgerRequest, OpenedLedger>(new() {Name = "From the client"});

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
            Assert.That(outcome.Value.Id, Is.GreaterThan(3));
        });
    }

    [Test]
    public async Task TheClientFollowsAPendingCommandToItsOutcome()
    {
        await using var server = await Server.Start(database, _ => _.CommandSyncWindow = TimeSpan.Zero);
        var gate = server.Gate();
        var client = server.Client();
        client.CommandWait = TimeSpan.FromMilliseconds(100);

        var outcome = await client.SendCommandAsync(new RenameLedgerRequest {Id = 1, Name = "Cash"});
        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Pending));
        Assert.That(client.PendingWork.PendingCount, Is.EqualTo(1));

        gate.SetResult();
        var final = await outcome.Completion.WaitAsync(patience);

        Assert.That(final.Status, Is.EqualTo(ScryCommandStatus.Completed));
    }

    // The server ends a pending command's stream at its lifetime; the client asks for it again by id
    // and is answered on the same terms, and the consumer sees an outcome as if nothing had happened.
    [Test]
    public async Task TheClientAsksAgainForACommandWhoseStreamTheServerEnded()
    {
        await using var server = await Server.Start(
            database,
            _ =>
            {
                _.CommandSyncWindow = TimeSpan.Zero;
                _.SubscriptionLifetime = TimeSpan.FromMilliseconds(250);
            });
        var gate = server.Gate();
        var client = server.Client();
        client.CommandWait = TimeSpan.Zero;
        var reattached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CommandActivity += _ =>
        {
            if (_.Kind == ScryCommandActivityKind.Reattaching)
            {
                reattached.TrySetResult();
            }
        };

        var outcome = await client.SendCommandAsync(new RenameLedgerRequest {Id = 2, Name = "Bank"});
        await reattached.Task.WaitAsync(patience);
        gate.SetResult();

        Assert.That((await outcome.Completion.WaitAsync(patience)).Status, Is.EqualTo(ScryCommandStatus.Completed));
    }

    [Test]
    public async Task ARowTheClientMayNotActOnIsAFailedOutcome()
    {
        await using var server = await Server.Start(database);

        var outcome = await server.Client().SendCommandAsync(new RenameLedgerRequest {Id = 3, Name = "Unlocked"});

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Failed));
            Assert.That(outcome.Error, Is.EqualTo(ScryCommandNotFoundException.TargetMessage));
        });
    }

    [Test]
    public async Task ACallerThePolicyRefusesIsToldSoByTheClient()
    {
        await using var server = await Server.Start(database, authenticate: true);

        Assert.ThrowsAsync<ScryPermissionException>(() => server.Client("mallory").SendCommandAsync(new RenameLedgerRequest {Id = 1, Name = "Mine"}));
    }

    [Test]
    public async Task TheClientIsToldWhatItsCallerMaySend()
    {
        await using var server = await Server.Start(database, authenticate: true);
        var alice = server.Client("alice");
        var mallory = server.Client("mallory");

        await alice.Ready;
        await mallory.Ready;

        Assert.Multiple(() =>
        {
            Assert.That(alice.Can("RenameLedger"), Is.True);
            Assert.That(mallory.Can("RenameLedger"), Is.False);
            Assert.That(mallory.Can("OpenLedger"), Is.True);
        });
    }

    [Test]
    public async Task AServerWithCommandsOffIsSaidToBeOneByTheClient()
    {
        await using var server = await Server.Start(database, _ => _.MaxPendingCommands = 0);
        var client = server.Client();

        Assert.ThrowsAsync<NotSupportedException>(() => client.SendCommandAsync(new OpenLedgerRequest {Name = "x"}));
        await client.Ready;
        Assert.That(client.Can("OpenLedger"), Is.False);
    }

    static async Task<string> Next(IAsyncEnumerator<SseItem<string>> events)
    {
        while (true)
        {
            Assert.That(await events.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
            if (events.Current.EventType == ScryLive.Result)
            {
                return events.Current.Data;
            }
        }
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static CommandRequest Command(string name, object payload, Guid? id = null) =>
        CommandRequest.Create(name, id ?? Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, ScryJson.Options));

    sealed class Server(WebApplication app, HttpClient http, LedgerGate gate) :
        IAsyncDisposable
    {
        public HttpClient Http => http;

        public static async Task<Server> Start(
            SqlDatabase<LedgerContext> database,
            Action<ScryOptions>? configure = null,
            bool authenticate = false,
            bool requireWriter = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var gate = new LedgerGate();
            builder.Services.AddSingleton(gate);
            builder.Services.AddScoped<LedgerCaller>();
            builder.Services.AddDbContext<LedgerContext>(
                (services, options) => options
                    .UseSqlServer(database.ConnectionString)
                    .AddInterceptors(services.GetRequiredService<ScryChangeInterceptor>()));
            builder.Services.AddScry<LedgerContext>(options =>
            {
                options.AllowUnmappedSources = true;
                options.MaxPendingCommands = 100;
                options.MaxSubscriptions = 10;
                options.SubscriptionThrottle = TimeSpan.Zero;
                options.SubscriptionPollInterval = null;
                configure?.Invoke(options);
            });
            builder.Services.AddScoped<ICommandHandler<RenameLedger>, RenameLedgerHandler>();
            builder.Services.AddScoped<ICommandHandler<OpenLedger, LedgerOpened>, OpenLedgerHandler>();
            if (authenticate)
            {
                builder.Services
                    .AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, HeaderUserHandler>("Test", _ => { });
                builder.Services
                    .AddAuthorizationBuilder()
                    .AddPolicy("Writer", policy => policy.RequireAssertion(_ => !requireWriter));
            }

            var app = builder.Build();
            if (authenticate)
            {
                app.UseAuthentication();
                app.UseAuthorization();
            }

            app.Use(LedgerCaller.FromRequest);

            var endpoints = app.MapScry("/api/query");
            if (authenticate)
            {
                endpoints.RequireAuthorization("Writer");
            }

            await app.StartAsync();
            return new(app, app.GetTestClient(), gate);
        }

        // A client of this server, calling as the named user.
        public ScryClient Client(string? user = null)
        {
            var client = app.GetTestClient();
            if (user is not null)
            {
                client.DefaultRequestHeaders.Add(userHeader, user);
            }

            return ScryClient.ForHttp(client, "/api/query");
        }

        // Holds every rename open until the test completes what this returns.
        public TaskCompletionSource Gate()
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.Held = held;
            return held;
        }

        public Task<HttpResponseMessage> Post(CommandRequest request, string? user = null) =>
            Send(HttpMethod.Post, "/api/query/command", ScryJson.SerializeToUtf8(request), user);

        public Task<HttpResponseMessage> PostRaw(string body) =>
            Send(HttpMethod.Post, "/api/query/command", System.Text.Encoding.UTF8.GetBytes(body), user: null);

        public Task<HttpResponseMessage> Get(string path, string? user = null) =>
            Send(HttpMethod.Get, path, body: null, user);

        async Task<HttpResponseMessage> Send(HttpMethod method, string path, byte[]? body, string? user, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        {
            var message = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                message.Content = new ByteArrayContent(body)
                {
                    Headers = {ContentType = new MediaTypeHeaderValue("application/json")}
                };
            }

            if (user is not null)
            {
                message.Headers.Add(userHeader, user);
            }

            return await http.SendAsync(message, completion);
        }

        public async Task<Events> Stream(CommandRequest request, string? user = null)
        {
            var response = await Send(HttpMethod.Post, "/api/query/command", ScryJson.SerializeToUtf8(request), user, HttpCompletionOption.ResponseHeadersRead);
            return await Events.Open(response);
        }

        public async Task<Events> StreamReceipt(Guid id)
        {
            var response = await Send(HttpMethod.Get, $"/api/query/command/{id:D}", body: null, user: null, HttpCompletionOption.ResponseHeadersRead);
            return await Events.Open(response);
        }

        // Each seeded ledger's capability to be renamed, as a caller reads it through a query. Seeded
        // only: other tests open ledgers of their own in the shared database.
        public async Task<List<(int Id, bool Can)>> Capabilities(string user)
        {
            var request = QueryRequest.Create(
                "Ledger",
                [
                    new WhereOp(new BinaryNode(BinaryOp.LessThanOrEqual, new MemberNode(["Id"]), new ConstNode("3", ClrTypeTag.Int32))),
                    new OrderByOp(new MemberNode(["Id"]), Descending: false),
                    new SelectOp(new([new("id", new NodeValue(new MemberNode(["Id"]))), new("can", new NodeValue(new MemberNode(["CanRenameLedger"])))]))
                ]);
            using var response = await Send(HttpMethod.Post, "/api/query", ScryJson.SerializeToUtf8(request), user);
            var answer = ScryJson.DeserializeResponse(await response.Content.ReadAsStringAsync());
            return [.. answer.Payload.EnumerateArray().Select(_ => (_.GetProperty("id").GetInt32(), _.GetProperty("can").GetBoolean()))];
        }

        public async ValueTask DisposeAsync()
        {
            gate.Held?.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            http.Dispose();
        }
    }

    sealed class Events(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> events) :
        IAsyncDisposable
    {
        public HttpResponseMessage Response => response;

        public static async Task<Events> Open(HttpResponseMessage response)
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo(ScryLive.ContentType));
            var stream = await response.Content.ReadAsStreamAsync();
            return new(response, SseParser.Create(stream).EnumerateAsync().GetAsyncEnumerator());
        }

        public async Task<SseItem<string>> Next()
        {
            while (true)
            {
                Assert.That(await events.MoveNextAsync().AsTask().WaitAsync(patience), Is.True, "The stream ended.");
                if (events.Current.EventType != ScryLive.Ping)
                {
                    return events.Current;
                }
            }
        }

        public async Task<bool> Ended() =>
            !await events.MoveNextAsync().AsTask().WaitAsync(patience);

        public async ValueTask DisposeAsync()
        {
            response.Dispose();
            try
            {
                await events.DisposeAsync();
            }
            catch (Exception)
            {
                // The response was closed under it, which is how a client leaves.
            }
        }
    }
}

/// <summary>The ledger model's database: three ledgers, one of them locked against renaming.</summary>
static class LedgerData
{
    public static SqlInstance<LedgerContext> Instance { get; } = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: async context =>
        {
            await context.Database.EnsureCreatedAsync();
            context.Ledgers.AddRange(
                new() {Id = 1, Name = "Cash"},
                new() {Id = 2, Name = "Bank"},
                // Readable, and closed to renaming by the command's row condition.
                new() {Id = 3, Name = "Archive", Locked = true});
            await context.SaveChangesAsync();
        });
}

/// <summary>
/// Names the caller from a header — a test's stand-in for real authentication, which a real host reads
/// from a ticket and never from anything the caller chose.
/// </summary>
sealed class HeaderUserHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) :
    AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Header = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new(ClaimTypes.Name, user.ToString())], "Test");
        return Task.FromResult(AuthenticateResult.Success(new(new(identity), "Test")));
    }
}

// The client's side of the two commands, as the generator would emit them.
[ScryCommand("RenameLedger", Target = "Ledger", Keys = ["Id"])]
public sealed class RenameLedgerRequest
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

[ScryCommand("OpenLedger", Result = typeof(OpenedLedger))]
public sealed class OpenLedgerRequest
{
    public string Name { get; init; } = "";
}

public sealed class OpenedLedger
{
    public int Id { get; init; }
}

[Queryable]
public class Ledger
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Locked { get; set; }
}

/// <summary>A targeted command: refused outright for mallory, and row by row for a locked ledger.</summary>
[Command(typeof(Ledger), Policy = typeof(RenameLedgerPolicy))]
public class RenameLedger
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [CommandIgnore]
    public string RenamedBy { get; set; } = "";
}

public sealed class RenameLedgerPolicy :
    ICommandPolicy<RenameLedger, Ledger>
{
    public bool Allow(ScryPolicyContext context) =>
        context.Services.GetRequiredService<LedgerCaller>().Name != "mallory";

    public Expression<Func<Ledger, bool>> Rows(ScryPolicyContext context) =>
        _ => !_.Locked;
}

/// <summary>An untargeted command answering with a result.</summary>
[Command(Result = typeof(LedgerOpened))]
public class OpenLedger
{
    public string Name { get; set; } = "";
}

public class LedgerOpened
{
    public int Id { get; set; }
}

/// <summary>
/// Who is calling, as the host decided it: filled per request by a middleware, and per hub call by a
/// hub filter, since a hub call has no request of its own for anything to read the user from.
/// </summary>
public sealed class LedgerCaller
{
    public string? Name { get; set; }

    public static Task FromRequest(HttpContext context, RequestDelegate next)
    {
        context.RequestServices.GetRequiredService<LedgerCaller>().Name = context.User.Identity?.Name;
        return next(context);
    }
}

sealed class LedgerGate
{
    public TaskCompletionSource? Held { get; set; }
}

sealed class RenameLedgerHandler(LedgerContext data, LedgerGate gate) :
    ICommandHandler<RenameLedger>
{
    public async Task Handle(RenameLedger command, ScryCommandContext context, CancellationToken cancel)
    {
        if (gate.Held is { } held)
        {
            await held.Task.WaitAsync(cancel);
        }

        data.Ledgers.Single(_ => _.Id == command.Id).Name = command.Name;
    }
}

sealed class OpenLedgerHandler(LedgerContext data) :
    ICommandHandler<OpenLedger, LedgerOpened>
{
    public async Task<LedgerOpened> Handle(OpenLedger command, ScryCommandContext context, CancellationToken cancel)
    {
        var ledger = new Ledger
        {
            Id = data.Ledgers.Max(_ => _.Id) + 1,
            Name = command.Name
        };
        data.Ledgers.Add(ledger);
        await data.SaveChangesAsync(cancel);
        return new()
        {
            Id = ledger.Id
        };
    }
}

public sealed class LedgerContext(Microsoft.EntityFrameworkCore.DbContextOptions<LedgerContext> options) :
    Microsoft.EntityFrameworkCore.DbContext(options)
{
    public Microsoft.EntityFrameworkCore.DbSet<Ledger> Ledgers { get; set; } = null!;

    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder builder) =>
        builder.Entity<Ledger>()
            .Property(_ => _.Id)
            .ValueGeneratedNever();
}
