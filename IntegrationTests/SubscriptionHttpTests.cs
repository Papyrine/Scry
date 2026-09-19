// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using System.Net.ServerSentEvents;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
using SampleContext = Sample.Model.SampleContext;

/// <summary>
/// A live query from the far side of HTTP: what is a status and what is an event, what the events
/// carry, and what the response tells everything between the server and the caller about itself.
/// </summary>
/// <remarks>
/// Read with the platform's own server-sent-events parser rather than the Scry client, so that what is
/// pinned is the wire. Each test hosts a server of its own, since nearly every one of them is about an
/// option; they share a database, which the tests that write to it keep their hands off each other in
/// by writing to regions of their own.
/// </remarks>
[TestFixture]
public class SubscriptionHttpTests
{
    static readonly SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    SqlDatabase<SampleContext> database = null!;

    [OneTimeSetUp]
    public async Task BuildDatabase() =>
        database = await sqlInstance.Build();

    [OneTimeTearDown]
    public async Task DropDatabase() =>
        await database.DisposeAsync();

    // Byte for byte what the query endpoint answers: a client reads an event exactly as it reads a
    // response, and needs nothing new to do it.
    [Test]
    public async Task TheFirstEventIsTheAnswerTheQueryEndpointGives()
    {
        await using var server = await Server.Start(database);
        var request = Amounts("North");

        using var content = Json(request);
        using var once = await server.Http.PostAsync("/api/query", content);
        var expected = await once.Content.ReadAsStringAsync();

        await using var live = await LiveStream.Open(server.Http, request);
        var first = await live.Next();

        Assert.Multiple(() =>
        {
            Assert.That(first.EventType, Is.EqualTo(ScryLive.Result));
            Assert.That(first.Data, Is.EqualTo(expected));
            Assert.That(first.EventId, Is.Not.Empty);
        });
    }

    [Test]
    public async Task AWriteSendsTheNewAnswer()
    {
        await using var server = await Server.Start(database);
        await using var live = await LiveStream.Open(server.Http, Amounts("Pushed"));
        var first = await live.Next();

        await server.AddOrder("Pushed", 12.5m);
        var second = await live.Next();

        Assert.Multiple(() =>
        {
            Assert.That(Rows(first), Is.Empty);
            Assert.That(Rows(second), Is.EqualTo([12.5m]));
            Assert.That(second.EventId, Is.Not.EqualTo(first.EventId));
        });
    }

    // Reconnecting — after a dropped connection, or because the server ended the stream to bound its
    // lifetime — usually finds what it left. Saying so costs a line; sending it again costs the rows.
    [Test]
    public async Task AskingAgainWithTheLastAnswersIdIsToldNothingChanged()
    {
        await using var server = await Server.Start(database);
        var request = Amounts("North");
        string id;
        await using (var live = await LiveStream.Open(server.Http, request))
        {
            id = (await live.Next()).EventId!;
        }

        await using var again = await LiveStream.Open(server.Http, request, id);
        var first = await again.Next();

        Assert.Multiple(() =>
        {
            Assert.That(first.EventType, Is.EqualTo(ScryLive.Unchanged));
            Assert.That(first.Data, Is.Empty);
        });
    }

    [Test]
    public async Task AskingAgainWithAStaleIdIsSentTheAnswer()
    {
        await using var server = await Server.Start(database);
        await using var live = await LiveStream.Open(server.Http, Amounts("North"), "not-the-last-answer");

        Assert.That((await live.Next()).EventType, Is.EqualTo(ScryLive.Result));
    }

    [Test]
    public async Task AnIdleStreamIsSentHeartbeats()
    {
        await using var server = await Server.Start(database, _ => _.SubscriptionHeartbeat = TimeSpan.FromMilliseconds(100));
        await using var live = await LiveStream.Open(server.Http, Amounts("North"));
        await live.Next();

        var heartbeat = await live.Next(skipPings: false);

        Assert.That(heartbeat.EventType, Is.EqualTo(ScryLive.Ping));
    }

    // Authorization is decided once per request, and a live query is one request. Ending it is what
    // makes the caller prove who they are again.
    [Test]
    public async Task TheStreamEndsAtItsLifetimeAndSaysToAskAgain()
    {
        await using var server = await Server.Start(database, _ => _.SubscriptionLifetime = TimeSpan.FromMilliseconds(300));
        await using var live = await LiveStream.Open(server.Http, Amounts("North"));
        await live.Next();

        var last = await live.Next();
        var end = ScryJson.DeserializeLiveEnd(Encoding.UTF8.GetBytes(last.Data));

        Assert.Multiple(() =>
        {
            Assert.That(last.EventType, Is.EqualTo(ScryLive.End));
            Assert.That(end.Reconnect, Is.True);
            Assert.That(end.Reason, Is.EqualTo("lifetime"));
        });
        Assert.That(await live.Ended(), Is.True);
    }

    // A host shutting down waits for its requests to finish, and a live query never would. So it is
    // ended, and ended as something to ask again after: the next node along will answer.
    [Test]
    public async Task TheStreamEndsWhenTheServerIsStoppingAndSaysToAskAgain()
    {
        await using var server = await Server.Start(database);
        await using var live = await LiveStream.Open(server.Http, Amounts("North"));
        await live.Next();

        server.BeginStopping();
        var last = await live.Next();
        var end = ScryJson.DeserializeLiveEnd(Encoding.UTF8.GetBytes(last.Data));

        Assert.Multiple(() =>
        {
            Assert.That(last.EventType, Is.EqualTo(ScryLive.End));
            Assert.That(end.Reconnect, Is.True);
            Assert.That(end.Reason, Is.EqualTo("shutdown"));
        });
        Assert.That(await live.Ended(), Is.True);
    }

    [Test]
    public async Task TheStreamEndsWhenTheTicketThatOpenedItExpires()
    {
        await using var server = await Server.Start(
            database,
            configure: null,
            ticketExpiresIn: TimeSpan.FromMilliseconds(300));
        await using var live = await LiveStream.Open(server.Http, Amounts("North"));
        await live.Next();

        var last = await live.Next();

        Assert.Multiple(() =>
        {
            Assert.That(last.EventType, Is.EqualTo(ScryLive.End));
            Assert.That(ScryJson.DeserializeLiveEnd(Encoding.UTF8.GetBytes(last.Data)).Reason, Is.EqualTo("expired"));
        });
    }

    // The first answer is made before the response is committed, so a refusal is still a status with
    // a body, exactly as the query endpoint gives it.
    [Test]
    public async Task ARejectedQueryIsAStatusNotAnEvent()
    {
        await using var server = await Server.Start(database);
        using var content = Json(QueryRequest.Create("Nothing", []));
        using var response = await server.Http.PostAsync("/api/query/subscribe", content);
        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(error?.Code, Is.EqualTo(ScryErrorCode.Validation));
        });
    }

    [Test]
    public async Task AMalformedRequestIsAStatus()
    {
        await using var server = await Server.Start(database);
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        using var response = await server.Http.PostAsync("/api/query/subscribe", content);
        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(error?.Code, Is.EqualTo(ScryErrorCode.WireFormat));
        });
    }

    // A cross-site form can send JSON-shaped text; it cannot declare it as JSON.
    [Test]
    public async Task ABodyNotDeclaredJsonIsRefusedUnread()
    {
        await using var server = await Server.Start(database);
        using var content = new StringContent(ScryJson.Serialize(Amounts("North")), Encoding.UTF8, "text/plain");
        using var response = await server.Http.PostAsync("/api/query/subscribe", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    [Test]
    public async Task OnePastTheServersLimitIsToldToComeBack()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 1);
        await using var held = await LiveStream.Open(server.Http, Amounts("North"));
        await held.Next();

        using var content = Json(Amounts("North"));
        using var response = await server.Http.PostAsync("/api/query/subscribe", content);
        var error = ScryJson.TryDeserializeError(await response.Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(response.Headers.RetryAfter, Is.Not.Null);
            Assert.That(error?.Code, Is.EqualTo(ScryErrorCode.SubscriptionLimit));
        });
    }

    [Test]
    public async Task OnePastACallersLimitIsThatCallersToFix()
    {
        await using var server = await Server.Start(
            database,
            options =>
            {
                options.MaxSubscriptionsPerCaller = 1;
                options.SubscriptionCaller = _ => "alice";
            });
        await using var held = await LiveStream.Open(server.Http, Amounts("North"));
        await held.Next();

        using var content = Json(Amounts("North"));
        using var response = await server.Http.PostAsync("/api/query/subscribe", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }

    // A closed stream gives its place back, which is what makes the limit a limit on what is open.
    [Test]
    public async Task AClosedStreamGivesItsPlaceBack()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 1);
        await using (var held = await LiveStream.Open(server.Http, Amounts("North")))
        {
            await held.Next();
        }

        // The server learns the client went when its next write fails or its token fires, which is
        // soon rather than at once.
        var started = DateTime.UtcNow;
        while (true)
        {
            using var content = Json(Amounts("North"));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/query/subscribe")
            {
                Content = content
            };
            using var response = await server.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(20)), "The place was never given back.");
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task TheResponseSaysItIsNeitherToBeKeptNorHeldBack()
    {
        await using var server = await Server.Start(database);
        await using var live = await LiveStream.Open(server.Http, Amounts("North"));
        var response = live.Response;

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(ScryLive.ContentType));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(response.Headers.GetValues("X-Accel-Buffering"), Is.EqualTo(["no"]));
            Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(["identity"]));
            Assert.That(response.Headers.Contains(WireFormat.SchemaStampHeader), Is.True);
        });
    }

    // The status is long since sent, so a failure has only the stream to be said in. What it says is
    // what the status would have: the client's own doing in full, anything else as the fixed text.
    [Test]
    public async Task AFailureAfterTheFirstAnswerIsSaidInTheStream()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptionBytes = 256);
        await using var live = await LiveStream.Open(server.Http, Amounts("Outgrown"));
        await live.Next();

        for (var index = 0; index < 40; index++)
        {
            await server.AddOrder("Outgrown", index);
        }

        SseItem<string> last;
        do
        {
            last = await live.Next();
        }
        while (last.EventType == ScryLive.Result);

        var error = ScryJson.TryDeserializeError(last.Data);
        Assert.Multiple(() =>
        {
            Assert.That(last.EventType, Is.EqualTo(ScryLive.Error));
            Assert.That(error?.Code, Is.EqualTo(ScryErrorCode.Validation));
            Assert.That(error?.Error, Does.Contain("256 bytes"));
        });
        Assert.That(await live.Ended(), Is.True);
    }

    // Projecting a [Sensitive] member marks the response no-store on every run, and the headers of a
    // response already under way refuse the write. Later runs are handed headers of their own.
    [Test]
    public async Task AQueryThatMarksItsResponseSurvivesItsSecondRun()
    {
        await using var server = await Server.Start(database);
        var request = server.Query.Employee
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Id, _.Password})
            .ToScryRequest();
        await using var live = await LiveStream.Open(server.Http, request);
        await live.Next();

        await server.Write(async context =>
        {
            var employee = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
                context.Employees.OrderBy(_ => _.Id));
            employee.Password = $"changed-{Guid.NewGuid():N}";
        });

        Assert.That((await live.Next()).EventType, Is.EqualTo(ScryLive.Result));
    }

    // Off is the default, and off is absent rather than guarded: there is no handler to reach.
    [Test]
    public async Task WithNoLimitSetThereIsNoRoute()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 0);
        using var content = Json(Amounts("North"));
        using var response = await server.Http.PostAsync("/api/query/subscribe", content);

        Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed));
    }

    /// <summary>
    /// The route is mapped inside <c>MapScry</c>, so a convention applied to what it returns reaches
    /// it. A deployment that guards its queries and leaves this open would be streaming the answers
    /// the guard exists to protect.
    /// </summary>
    [Test]
    public async Task AuthorizationReachesTheSubscribeRoute()
    {
        await using var server = await Server.Start(database, configure: null, ticketExpiresIn: null, refuseEveryone: true);
        using var content = Json(Amounts("North"));
        using var response = await server.Http.PostAsync("/api/query/subscribe", content);

        Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden));
    }

    // From here down, the far side is the generated client rather than a parser: LINQ in, rows out, and
    // rows out again when they change, over the real endpoint.
    [Test]
    public async Task TheGeneratedClientIsHandedEachChange()
    {
        await using var server = await Server.Start(database);
        await using var answers = server.Query.Order
            .Where(_ => _.Region == "ClientRows")
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Amount})
            .Live()
            .GetAsyncEnumerator();

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current, Is.Empty);

        await server.AddOrder("ClientRows", 7.25m);

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current.Select(_ => _.Amount), Is.EqualTo([7.25m]));
    }

    [Test]
    public async Task TheGeneratedClientIsHandedALiveCount()
    {
        await using var server = await Server.Start(database);
        await using var answers = server.Query.Order
            .LiveCount(_ => _.Region == "ClientCount")
            .GetAsyncEnumerator();

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current, Is.Zero);

        await server.AddOrder("ClientCount", 1m);

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current, Is.EqualTo(1));
    }

    // The server ends every stream at its lifetime. The client asks again, names the answer it holds,
    // is told it still stands — and the consumer sees a change arrive as if nothing had happened.
    [Test]
    public async Task AStreamTheServerEndedIsAskedForAgainWithoutTheConsumerNoticing()
    {
        await using var server = await Server.Start(database, _ => _.SubscriptionLifetime = TimeSpan.FromMilliseconds(250));
        List<int> counts = [];
        List<ScrySubscriptionState> states = [];

        await using var subscription = server.Query.Order
            .LiveCount(_ => _.Region == "ClientReconnect")
            .Subscribe(
                count =>
                {
                    lock (counts)
                    {
                        counts.Add(count);
                    }
                });
        subscription.StateChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };

        await Until(() => states.Contains(ScrySubscriptionState.Reconnecting), states);
        await server.AddOrder("ClientReconnect", 1m);
        await Until(() => counts.Count == 2, counts);

        lock (counts)
        {
            // Once each: asking again did not deliver the answer already held a second time.
            Assert.That(counts, Is.EqualTo([0, 1]));
        }
    }

    // A deployment, from where the consumer sits: the server goes away, there is nothing listening for
    // a while, and another comes up on the same address. Over a real socket, since a refused connection
    // is the part an in-memory server cannot produce. What was written while nothing was listening is
    // the first thing the new server says.
    [Test]
    public async Task AServerThatRestartsIsFoundAgainWithoutTheConsumerNoticing()
    {
        var port = FreePort();
        using var http = new HttpClient
        {
            BaseAddress = new($"http://127.0.0.1:{port}")
        };
        var client = ScryClient.ForHttp(http, "/api/query");
        client.Reconnect = new Every(TimeSpan.FromMilliseconds(100));
        List<int> counts = [];
        List<ScrySubscriptionState> states = [];
        List<Exception> errors = [];

        var first = await Server.Start(database, port: port);
        await using var subscription = new ScryQuery(client).Order
            .LiveCount(_ => _.Region == "ServerRestart")
            .Subscribe(
                count =>
                {
                    lock (counts)
                    {
                        counts.Add(count);
                    }
                },
                error =>
                {
                    lock (counts)
                    {
                        errors.Add(error);
                    }
                });
        subscription.StateChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };
        await Until(() => counts.Count == 1, counts);

        await first.DisposeAsync();
        await Until(() => states.Contains(ScrySubscriptionState.Reconnecting), states);
        await using (var context = database.NewDbContext())
        {
            context.Orders.Add(
                new()
                {
                    Region = "ServerRestart",
                    Amount = 1m
                });
            await context.SaveChangesAsync();
        }

        await using var second = await Server.Start(database, port: port);
        await Until(() => counts.Count == 2, counts);

        lock (counts)
        {
            Assert.That(counts, Is.EqualTo([0, 1]));
            Assert.That(errors, Is.Empty);
        }
    }

    [Test]
    public async Task AServerNotServingLiveQueriesIsSaidToBeOne()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 0);
        await using var answers = server.Query.Order.LiveCount().GetAsyncEnumerator();

        var exception = Assert.ThrowsAsync<NotSupportedException>(async () => await answers.MoveNextAsync());

        Assert.That(exception!.Message, Does.Contain(nameof(ScryOptions.MaxSubscriptions)));
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static Task<bool> Next<T>(IAsyncEnumerator<T> answers) =>
        answers.MoveNextAsync().AsTask().WaitAsync(patience);

    static async Task Until(Func<bool> reached, object gate)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            lock (gate)
            {
                if (reached())
                {
                    return;
                }
            }

            Assert.That(DateTime.UtcNow - started, Is.LessThan(patience), "Waited too long.");
            await Task.Delay(20);
        }
    }

    // Free when asked, and nothing else on the machine is racing these tests for it.
    static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint) listener.LocalEndpoint).Port;
    }

    sealed class Every(TimeSpan delay) :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            delay;
    }

    // Only ever asked for a request, which is captured rather than sent.
    static readonly ScryQuery capture = new(ScryClient.ForHttp(new(), "/api/query"));

    static QueryRequest Amounts(string region) =>
        capture
            .Order
            .Where(_ => _.Region == region)
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Amount})
            .ToScryRequest();

    static decimal[] Rows(SseItem<string> item) =>
    [
        .. ScryJson.DeserializeResponse(item.Data)
            .Payload
            .EnumerateArray()
            .Select(_ => _.GetProperty("amount").GetDecimal())
    ];

    static StringContent Json(QueryRequest request) =>
        new(ScryJson.Serialize(request), Encoding.UTF8, "application/json");

    /// <summary>One open live query, read an event at a time.</summary>
    sealed class LiveStream(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> events) :
        IAsyncDisposable
    {
        static TimeSpan patience = TimeSpan.FromSeconds(20);

        public HttpResponseMessage Response => response;

        public static async Task<LiveStream> Open(HttpClient http, QueryRequest request, string? lastEventId = null)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "/api/query/subscribe")
            {
                Content = Json(request)
            };
            if (lastEventId is not null)
            {
                message.Headers.Add(ScryLive.LastEventIdHeader, lastEventId);
            }

            var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await Body(response));
            var stream = await response.Content.ReadAsStreamAsync();
            return new(response, SseParser.Create(stream).EnumerateAsync().GetAsyncEnumerator());
        }

        static async Task<string> Body(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return "";
            }

            return await response.Content.ReadAsStringAsync();
        }

        public async Task<SseItem<string>> Next(bool skipPings = true)
        {
            while (true)
            {
                Assert.That(await events.MoveNextAsync().AsTask().WaitAsync(patience), Is.True, "The stream ended.");
                if (!skipPings ||
                    events.Current.EventType != ScryLive.Ping)
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

    /// <summary>A server over the shared database, with live queries on and nothing to wait for.</summary>
    sealed class Server(WebApplication app, HttpClient http) :
        IAsyncDisposable
    {
        public HttpClient Http => http;

        public ScryQuery Query { get; } = new(ScryClient.ForHttp(http, "/api/query"));

        public static async Task<Server> Start(
            SqlDatabase<SampleContext> database,
            Action<ScryOptions>? configure = null,
            TimeSpan? ticketExpiresIn = null,
            bool refuseEveryone = false,
            int? port = null)
        {
            var builder = WebApplication.CreateBuilder();
            if (port is null)
            {
                builder.WebHost.UseTestServer();
            }
            else
            {
                // A real socket, for the one test about an address with nothing behind it.
                builder.WebHost.ConfigureKestrel(_ => _.ListenLocalhost(port.Value));
            }

            builder.Logging.ClearProviders();

            // The interceptor is what turns a save into a push, and is resolved rather than built so
            // that it reports to the same place the server listens.
            builder.Services.AddDbContext<SampleContext>(
                (services, options) => options
                    .UseSqlServer(database.ConnectionString)
                    .AddInterceptors(services.GetRequiredService<ScryChangeInterceptor>()));
            builder.Services.AddScry<SampleContext>(options =>
            {
                options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
                options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
                options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
                options.MaxSubscriptions = 10;
                options.SubscriptionThrottle = TimeSpan.Zero;
                options.SubscriptionPollInterval = null;
                configure?.Invoke(options);
            });

            var authenticated = ticketExpiresIn is not null || refuseEveryone;
            if (authenticated)
            {
                TicketHandler.ExpiresIn = ticketExpiresIn;
                builder.Services
                    .AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TicketHandler>("Test", _ => { });
                builder.Services
                    .AddAuthorizationBuilder()
                    .AddPolicy("Reader", policy => policy.RequireAssertion(_ => !refuseEveryone));
            }

            var app = builder.Build();
            if (authenticated)
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapScry("/api/query").RequireAuthorization("Reader");
            }
            else
            {
                app.MapScry("/api/query");
            }

            await app.StartAsync();
            return new(
                app,
                port is null
                    ? app.GetTestClient()
                    : new()
                    {
                        BaseAddress = new($"http://127.0.0.1:{port}")
                    });
        }

        /// <summary>What a host does first when asked to stop: says so, and then waits for its requests.</summary>
        public void BeginStopping() =>
            app.Lifetime.StopApplication();

        public Task AddOrder(string region, decimal amount) =>
            Write(context =>
            {
                context.Orders.Add(
                    new()
                    {
                        Region = region,
                        Amount = amount
                    });
                return Task.CompletedTask;
            });

        public async Task Write(Func<SampleContext, Task> write)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<SampleContext>();
            await write(context);
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>Authenticates everyone, with a ticket that expires when the test says.</summary>
    sealed class TicketHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder) :
        AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public static TimeSpan? ExpiresIn { get; set; }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity([new("name", "reader")], "Test", "name", "role"));
            var properties = new AuthenticationProperties();
            if (ExpiresIn is { } span)
            {
                properties.ExpiresUtc = DateTimeOffset.UtcNow + span;
            }

            return Task.FromResult(AuthenticateResult.Success(new(principal, properties, "Test")));
        }
    }
}
