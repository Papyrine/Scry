// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
using SampleContext = Sample.Model.SampleContext;
// These drive a live query's enumerator by hand and end it by disposing it, which is what the
// await using below is for — a live query runs "until cancel is cancelled or the enumeration is
// abandoned", and these abandon it. Where a token is wanted it goes to the call that opens the
// query, whose own parameter carries it into the iterator.
// ReSharper disable MethodSupportsCancellation

/// <summary>
/// The same generated queries, round-tripped over a SignalR hub instead of HTTP: both adapter
/// packages at once, with nothing mapped but the hub. What has to hold is that nothing is different —
/// the rows, the failures and the exceptions they surface as, the limits — except the connection.
/// </summary>
/// <remarks>
/// Long polling, because the test server has no sockets. The hub protocol above it is the same
/// whichever transport carries it.
/// </remarks>
[NotInParallel]
[DependsOn<HttpRoundTripTests.StaleClient>(nameof(HttpRoundTripTests.StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
public class SignalRTests
{
    static readonly SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    static SqlDatabase<SampleContext> database = null!;

    [Before(Class)]
    public static async Task BuildDatabase() =>
        database = await sqlInstance.Build();

    [After(Class)]
    public static async Task DropDatabase() =>
        await database.DisposeAsync();

    record NameRow(string Name);

    [Test]
    public async Task RowsAScalarAndASingleRowComeBack()
    {
        await using var server = await Server.Start(database);

        var names = await server.Query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();
        var count = await server.Query.Department.CountAsync();
        var first = await server.Query.Employee
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .FirstOrDefaultAsync();

        using (Assert.Multiple())
        {
            await Assert.That(names.Select(_ => _.Name)).IsEquivalentTo(["Aaron", "Alice", "Carol"], CollectionOrdering.Matching);
            await Assert.That(count).IsEqualTo(2);
            await Assert.That(first?.Name).IsEqualTo("Aaron");
        }
    }

    [Test]
    public async Task ABatchComesBack()
    {
        await using var server = await Server.Start(database);
        var batch = server.Client.Batch();

        var names = server.Query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .InBatch(batch)
            .ToListAsync();
        var count = server.Query.Department
            .InBatch(batch)
            .CountAsync();
        await batch.SendAsync();

        using (Assert.Multiple())
        {
            await Assert.That((await names).Select(_ => _.Name)).IsEquivalentTo(["Aaron", "Alice", "Carol"], CollectionOrdering.Matching);
            await Assert.That(await count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task RowsAreStreamed()
    {
        await using var server = await Server.Start(database);
        List<string> names = [];

        await foreach (var row in server.Query.Employee
                           .Where(_ => _.Active)
                           .OrderBy(_ => _.Name)
                           .Select(_ => new NameRow(_.Name))
                           .ToAsyncEnumerable())
        {
            names.Add(row.Name);
        }

        await Assert.That(names).IsEquivalentTo(["Aaron", "Alice", "Carol"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ALiveQueryIsAnsweredAgainWhenItChanges()
    {
        await using var server = await Server.Start(database);
        await using var answers = server.Query.Order
            .LiveCount(_ => _.Region == "HubLive")
            .GetAsyncEnumerator();

        await Assert.That(await Next(answers)).IsTrue();
        await Assert.That(answers.Current).IsZero();

        await server.AddOrder("HubLive");

        await Assert.That(await Next(answers)).IsTrue();
        await Assert.That(answers.Current).IsEqualTo(1);
    }

    // A live query asked while the hub connection is down — as the client asks again at once when a
    // connection ends, before it is back — is refused as a call on a closed connection. That is a
    // connection failure to ask again past, as it is for a pending command, not one that ends the live
    // query. Asked on a stopped connection here because stopping one does not end its streams in this
    // host, so the drop itself cannot be what leads to the asking.
    [Test]
    public async Task ALiveQueryOutlivesTheConnection()
    {
        await using var server = await Server.Start(database);
        server.Client.Reconnect = new Every(TimeSpan.FromMilliseconds(50));
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var down = new TaskCompletionSource<ScrySubscriptionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Client.LiveActivity += _ =>
        {
            seen.Enqueue($"{_.State} {_.Failure?.GetType().Name}");
            if (_ is {State: ScrySubscriptionState.Reconnecting, Failure: IOException} or {State: ScrySubscriptionState.Faulted})
            {
                down.TrySetResult(_.State);
            }
        };

        await server.Connection.StopAsync();
        await using var answers = server.Query.Order
            .LiveCount(_ => _.Region == "HubReconnect")
            .GetAsyncEnumerator();
        var moving = Next(answers);
        await Task.WhenAny(down.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await Assert.That(down.Task.IsCompleted).IsTrue().Because(string.Join(" | ", seen));
        await Assert.That(await down.Task).IsEqualTo(ScrySubscriptionState.Reconnecting);

        await server.Connection.StartAsync();
        await Assert.That(await moving).IsTrue();
        await Assert.That(answers.Current).IsZero();
    }

    sealed class Every(TimeSpan delay) :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            delay;
    }

    // Caught by the code that catches it over HTTP: the same type, and the same code on it.
    [Test]
    public async Task ARejectionSurfacesAsItDoesOverHttp()
    {
        await using var server = await Server.Start(database);

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(
            () => server.Client.Source<NameRow>("Nothing").ToListAsync()))!;

        using (Assert.Multiple())
        {
            await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.Validation);
            await Assert.That(exception.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task ARejectionMidStreamSurfacesAsItDoesOverHttp()
    {
        await using var server = await Server.Start(database);

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(
            async () =>
            {
                await foreach (var _ in server.Client.Source<NameRow>("Nothing").ToAsyncEnumerable())
                {
                }
            }))!;

        await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.Validation);
    }

    // The sample model renames nothing, so a drifted client's stream is asked of the binary fixture's
    // model, where DocumentKind.Draft used to be Sketch. The hub's begin marker carries the rename as
    // the alias table, exactly as the stream endpoint's does, and a client generated before the rename
    // has to be handed it with every row — and the stamp beside it — or its rows throw over the hub
    // where they read over HTTP.
    [Test]
    public async Task ADriftedClientsStreamOverTheHubResolvesARenamedEnumValue()
    {
        await using var server = await BinaryServer.Start();
        var stale = ScrySignalRClient.Create(server.Connection);
        stale.SchemaStamp = "stamp-from-before-the-rename";

        var kinds = new List<Stale.DocumentKind>();
        await foreach (var row in stale.Source<Stale.Doc>("Document", ["Id", "Name", "Kind"])
                           .OrderBy(_ => _.Id)
                           .ToAsyncEnumerable())
        {
            kinds.Add(row.Kind);
        }

        using (Assert.Multiple())
        {
            await Assert.That(kinds).IsEquivalentTo([Stale.DocumentKind.Sketch, Stale.DocumentKind.Final], CollectionOrdering.Matching);
            await Assert.That(stale.SchemaStale).IsTrue();
        }
    }

    // The hub is handed a string and reads it with Scry's own reader. Bound by the hub's serializer
    // instead, a member the vocabulary does not name would have been skipped rather than refused.
    [Test]
    public async Task ARequestIsReadByScryAndNotByTheHub()
    {
        await using var server = await Server.Start(database);

        var answer = await server.Connection.InvokeAsync<string>(
            ScryHubProtocol.Query,
            """{"version":1,"root":"Department","pipeline":[{"$type":"count"}],"smuggled":true}""");
        var marker = ScryJson.DeserializeMarker(Encoding.UTF8.GetBytes(answer));

        using (Assert.Multiple())
        {
            await Assert.That(marker.Kind).IsEqualTo(ScryStream.Error);
            await Assert.That(marker.Code).IsEqualTo(ScryErrorCode.WireFormat);
        }
    }

    // SignalR puts no bound of its own on how many streams one client starts. The processor's does.
    [Test]
    public async Task TheLimitOnLiveQueriesAppliesOverTheHub()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 1);
        server.Client.Reconnect = new GivesUp();
        await using var held = server.Query.Order.LiveCount().GetAsyncEnumerator();
        await Assert.That(await Next(held)).IsTrue();

        await using var refused = server.Query.Order.LiveCount().GetAsyncEnumerator();
        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(async () => await refused.MoveNextAsync()))!;

        await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.SubscriptionLimit);
    }

    // A caller's own limit counts the caller ScryOptions.Caller names, over the hub as over HTTP:
    // a connection with no authenticated user is still the caller the server says it is, so it
    // cannot hold more live queries than that caller may by opening them over the hub.
    [Test]
    public async Task ACallersLimitOverTheHubIsTheCallerTheServerNames()
    {
        await using var server = await Server.Start(
            database,
            options =>
            {
                options.MaxSubscriptionsPerCaller = 1;
                options.Caller = _ => "alice";
            });
        server.Client.Reconnect = new GivesUp();
        await using var held = server.Query.Order.LiveCount().GetAsyncEnumerator();
        await Assert.That(await Next(held)).IsTrue();

        await using var refused = server.Query.Order.LiveCount().GetAsyncEnumerator();
        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(async () => await refused.MoveNextAsync()))!;

        await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.SubscriptionLimit);
    }

    // The hub hands back strings it holds whole, so the bound on what a response may carry applies to
    // it as it does over HTTP — to a query, to each entry of a batch, and to a stream as it goes.
    [Test]
    public async Task TheResponseLimitAppliesOverTheHub()
    {
        await using var server = await Server.Start(database, _ => _.MaxResponseBytes = 30);
        var names = server.Query.Employee
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name));

        var single = (await Assert.ThrowsExactlyAsync<ScryRequestException>(() => names.ToListAsync()))!;

        var batch = server.Client.Batch();
        var entry = names.InBatch(batch).ToListAsync();
        await batch.SendAsync();
        var batched = (await Assert.ThrowsExactlyAsync<ScryRequestException>(() => entry))!;

        var streamed = (await Assert.ThrowsExactlyAsync<ScryRequestException>(
            async () =>
            {
                await foreach (var _ in names.ToAsyncEnumerable())
                {
                }
            }))!;

        using (Assert.Multiple())
        {
            await Assert.That(single.Body).Contains("larger than this server allows (30 bytes)");
            await Assert.That(batched.Body).Contains("larger than this server allows (30 bytes)");
            await Assert.That(streamed.Code).IsEqualTo(ScryErrorCode.Validation);
        }
    }

    // Stopping the enumeration stops the hub's stream, which ends the subscription and gives its
    // place back. The second one waits for that, asking again under the default policy.
    [Test]
    public async Task AbandoningALiveQueryEndsItOnTheServer()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 1);
        await using (var held = server.Query.Order.LiveCount().GetAsyncEnumerator())
        {
            await Assert.That(await Next(held)).IsTrue();
        }

        await using var again = server.Query.Order.LiveCount().GetAsyncEnumerator();

        await Assert.That(await Next(again)).IsTrue();
    }

    [Test]
    public async Task AServerNotServingLiveQueriesSaysSoAndIsNotAskedAgain()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 0);
        await using var answers = server.Query.Order.LiveCount().GetAsyncEnumerator();

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(async () => await answers.MoveNextAsync()))!;

        await Assert.That(exception.Body).Contains(nameof(ScryOptions.MaxSubscriptions));
    }

    // Nothing here maps MapScry, so nothing but MapScryHub could have run the startup checks.
    [Test]
    public async Task TheStartupChecksRunWithoutTheHttpEndpoints()
    {
        var exception = (await Assert.ThrowsExactlyAsync<Exception>(
            async () =>
            {
                await using var server = await Server.Start(
                    database,
                    _ => _.AddPolicy<Sample.Model.Order, UnconstructablePolicy>());
            }))!;

        await Assert.That(exception.Message).Contains(nameof(UnconstructablePolicy));
    }

    [Test]
    public async Task AuthorizationOnTheHubRefusesTheConnection() =>
        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            async () =>
            {
                await using var server = await Server.Start(database, configure: null, refuseEveryone: true);
            });

    static Task<bool> Next<T>(IAsyncEnumerator<T> answers) =>
        answers.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

    sealed class GivesUp :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            null;
    }

    /// <summary>A server that maps the hub and nothing else, and a client connected to it.</summary>
    sealed class Server(WebApplication app, HubConnection connection) :
        IAsyncDisposable
    {
        public HubConnection Connection => connection;

        public ScryClient Client { get; } = ScrySignalRClient.Create(connection);

        public ScryQuery Query => new(Client);

        public static async Task<Server> Start(
            SqlDatabase<SampleContext> database,
            Action<ScryOptions>? configure = null,
            bool refuseEveryone = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSignalR();
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

            if (refuseEveryone)
            {
                builder.Services
                    .AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Test", _ => { });
                builder.Services.AddAuthorization();
            }

            var app = builder.Build();
            try
            {
                if (refuseEveryone)
                {
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.MapScryHub("/hub").RequireAuthorization();
                }
                else
                {
                    app.MapScryHub("/hub");
                }

                await app.StartAsync();

                var connection = new HubConnectionBuilder()
                    .WithUrl(
                        "http://localhost/hub",
                        options =>
                        {
                            options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                            options.Transports = HttpTransportType.LongPolling;
                        })
                    .Build();
                await connection.StartAsync();
                return new(app, connection);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public async Task AddOrder(string region)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<SampleContext>();
            context.Orders.Add(
                new()
                {
                    Region = region
                });
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    sealed class NobodyHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) :
        AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>The model a client generated before DocumentKind.Sketch became Draft reads with.</summary>
    static class Stale
    {
        public enum DocumentKind
        {
            Sketch,
            Final
        }

        public record Doc(int Id, string Name, DocumentKind Kind);
    }

    /// <summary>
    /// A server over the binary fixture's model that maps the hub and nothing else, and a connection to
    /// it. The database is one row of each kind, so a stream reads both values of the renamed enum.
    /// </summary>
    sealed class BinaryServer(WebApplication app, HubConnection connection, SqlDatabase<BinaryContext> database) :
        IAsyncDisposable
    {
        // A LocalDB instance of its own: the binary fixture builds this model's template in the default
        // one from a seed of its own, and two builds of one template at once deadlock inside SQL Server.
        static SqlInstance<BinaryContext> sqlInstance = new(
            constructInstance: _ => new(_.Options),
            buildTemplate: async context =>
            {
                await context.Database.EnsureCreatedAsync();
                context.Documents.AddRange(
                    new() {Name = "sketched", Kind = DocumentKind.Draft},
                    new() {Name = "finished", Kind = DocumentKind.Final});
                await context.SaveChangesAsync();
            },
            storage: Storage.FromSuffix<BinaryContext>("Hub"));

        public HubConnection Connection => connection;

        public static async Task<BinaryServer> Start()
        {
            var database = await sqlInstance.Build();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSignalR();
            builder.Services.AddDbContext<BinaryContext>(_ => _.UseSqlServer(database.ConnectionString));
            builder.Services.AddScry<BinaryContext>(_ => _.AllowUnmappedSources = true);

            var app = builder.Build();
            try
            {
                app.MapScryHub("/hub");
                await app.StartAsync();

                var connection = new HubConnectionBuilder()
                    .WithUrl(
                        "http://localhost/hub",
                        options =>
                        {
                            options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                            options.Transports = HttpTransportType.LongPolling;
                        })
                    .Build();
                await connection.StartAsync();
                return new(app, connection, database);
            }
            catch
            {
                await app.DisposeAsync();
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}

/// <summary>Never constructible: no parameterless constructor, and nothing registers what it asks for.</summary>
public sealed class UnconstructablePolicy(UnconstructablePolicy.Missing missing) :
    IReturnablePolicy<Sample.Model.Order>
{
    public sealed class Missing;

    public Missing Dependency { get; } = missing;

    public IQueryable<Sample.Model.Order> Filter(IQueryable<Sample.Model.Order> source, ScryPolicyContext context) =>
        source;
}
