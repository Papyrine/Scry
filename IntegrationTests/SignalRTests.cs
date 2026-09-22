// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own ToListAsync/CountAsync IQueryable extensions and collide with the Scry client terminals.
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
[TestFixture]
public class SignalRTests
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

        Assert.Multiple(() =>
        {
            Assert.That(names.Select(_ => _.Name), Is.EqualTo(["Aaron", "Alice", "Carol"]));
            Assert.That(count, Is.EqualTo(2));
            Assert.That(first?.Name, Is.EqualTo("Aaron"));
        });
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

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await names).Select(_ => _.Name), Is.EqualTo(["Aaron", "Alice", "Carol"]));
            Assert.That(await count, Is.EqualTo(2));
        });
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

        Assert.That(names, Is.EqualTo(["Aaron", "Alice", "Carol"]));
    }

    [Test]
    public async Task ALiveQueryIsAnsweredAgainWhenItChanges()
    {
        await using var server = await Server.Start(database);
        await using var answers = server.Query.Order
            .LiveCount(_ => _.Region == "HubLive")
            .GetAsyncEnumerator();

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current, Is.Zero);

        await server.AddOrder("HubLive");

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current, Is.EqualTo(1));
    }

    // Caught by the code that catches it over HTTP: the same type, and the same code on it.
    [Test]
    public async Task ARejectionSurfacesAsItDoesOverHttp()
    {
        await using var server = await Server.Start(database);

        var exception = Assert.ThrowsAsync<ScryRequestException>(
            () => server.Client.Source<NameRow>("Nothing").ToListAsync())!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo(ScryErrorCode.Validation));
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task ARejectionMidStreamSurfacesAsItDoesOverHttp()
    {
        await using var server = await Server.Start(database);

        var exception = Assert.ThrowsAsync<ScryRequestException>(
            async () =>
            {
                await foreach (var _ in server.Client.Source<NameRow>("Nothing").ToAsyncEnumerable())
                {
                }
            })!;

        Assert.That(exception.Code, Is.EqualTo(ScryErrorCode.Validation));
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

        Assert.Multiple(() =>
        {
            Assert.That(marker.Kind, Is.EqualTo(ScryStream.Error));
            Assert.That(marker.Code, Is.EqualTo(ScryErrorCode.WireFormat));
        });
    }

    // SignalR puts no bound of its own on how many streams one client starts. The processor's does.
    [Test]
    public async Task TheLimitOnLiveQueriesAppliesOverTheHub()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 1);
        server.Client.Reconnect = new GivesUp();
        await using var held = server.Query.Order.LiveCount().GetAsyncEnumerator();
        Assert.That(await Next(held), Is.True);

        await using var refused = server.Query.Order.LiveCount().GetAsyncEnumerator();
        var exception = Assert.ThrowsAsync<ScryRequestException>(async () => await refused.MoveNextAsync())!;

        Assert.That(exception.Code, Is.EqualTo(ScryErrorCode.SubscriptionLimit));
    }

    // Stopping the enumeration stops the hub's stream, which ends the subscription and gives its
    // place back. The second one waits for that, asking again under the default policy.
    [Test]
    public async Task AbandoningALiveQueryEndsItOnTheServer()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 1);
        await using (var held = server.Query.Order.LiveCount().GetAsyncEnumerator())
        {
            Assert.That(await Next(held), Is.True);
        }

        await using var again = server.Query.Order.LiveCount().GetAsyncEnumerator();

        Assert.That(await Next(again), Is.True);
    }

    [Test]
    public async Task AServerNotServingLiveQueriesSaysSoAndIsNotAskedAgain()
    {
        await using var server = await Server.Start(database, _ => _.MaxSubscriptions = 0);
        await using var answers = server.Query.Order.LiveCount().GetAsyncEnumerator();

        var exception = Assert.ThrowsAsync<ScryRequestException>(async () => await answers.MoveNextAsync())!;

        Assert.That(exception.Body, Does.Contain(nameof(ScryOptions.MaxSubscriptions)));
    }

    // Nothing here maps MapScry, so nothing but MapScryHub could have run the startup checks.
    [Test]
    public void TheStartupChecksRunWithoutTheHttpEndpoints()
    {
        var exception = Assert.ThrowsAsync<Exception>(
            async () =>
            {
                await using var server = await Server.Start(
                    database,
                    _ => _.AddPolicy<Sample.Model.Order, UnconstructablePolicy>());
            })!;

        Assert.That(exception.Message, Does.Contain(nameof(UnconstructablePolicy)));
    }

    [Test]
    public void AuthorizationOnTheHubRefusesTheConnection()
    {
        Assert.ThrowsAsync<HttpRequestException>(
            async () =>
            {
                await using var server = await Server.Start(database, configure: null, refuseEveryone: true);
            });
    }

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
        System.Text.Encodings.Web.UrlEncoder encoder) :
        AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
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
