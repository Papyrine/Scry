using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// Commands over a SignalR hub, through both adapter packages at once, with nothing mapped but the hub.
/// What has to hold is that nothing is different from HTTP — the outcomes, the refusals and the
/// exceptions they surface as, the limits — except the connection, which a pending command outlives.
/// </summary>
/// <remarks>Long polling, because the test server has no sockets.</remarks>
[TestFixture]
public class CommandHubTests
{
    SqlDatabase<LedgerContext> database = null!;

    [OneTimeSetUp]
    public async Task BuildDatabase() =>
        database = await LedgerData.Instance.Build();

    [OneTimeTearDown]
    public async Task DropDatabase() =>
        await database.DisposeAsync();

    [Test]
    public async Task ACommandDecidedWithinTheWindowIsItsOutcome()
    {
        await using var server = await Server.Start(database);

        var outcome = await server.Client.SendCommandAsync<OpenLedgerRequest, OpenedLedger>(new() {Name = "Over the hub"});

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Completed));
            Assert.That(outcome.Value.Id, Is.GreaterThan(3));
        });
    }

    [Test]
    public async Task APendingCommandIsFollowedToItsOutcome()
    {
        await using var server = await Server.Start(database, _ => _.CommandSyncWindow = TimeSpan.Zero);
        var gate = server.Gate();
        server.Client.CommandWait = TimeSpan.FromMilliseconds(100);

        var outcome = await server.Client.SendCommandAsync(new RenameLedgerRequest {Id = 1, Name = "Cash"});
        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Pending));

        gate.SetResult();

        Assert.That((await outcome.Completion.WaitAsync(patience)).Status, Is.EqualTo(ScryCommandStatus.Completed));
    }

    // The connection goes while the command is in flight. The command does not: the server finishes
    // it, and the client asks for it again by id once there is a connection to ask on.
    [Test]
    public async Task APendingCommandOutlivesTheConnection()
    {
        await using var server = await Server.Start(database, _ => _.CommandSyncWindow = TimeSpan.Zero);
        var gate = server.Gate();
        var client = server.Client;
        client.CommandWait = TimeSpan.Zero;
        client.Reconnect = new Every(TimeSpan.FromMilliseconds(50));
        var reattaching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CommandActivity += _ =>
        {
            if (_.Kind == ScryCommandActivityKind.Reattaching)
            {
                reattaching.TrySetResult();
            }
        };

        var outcome = await client.SendCommandAsync(new RenameLedgerRequest {Id = 2, Name = "Bank"});
        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Pending));

        await server.Connection.StopAsync();
        await reattaching.Task.WaitAsync(patience);
        gate.SetResult();
        await server.Connection.StartAsync();

        Assert.That((await outcome.Completion.WaitAsync(patience)).Status, Is.EqualTo(ScryCommandStatus.Completed));
    }

    [Test]
    public async Task ARowTheCallerMayNotActOnIsAFailedOutcome()
    {
        await using var server = await Server.Start(database);

        var outcome = await server.Client.SendCommandAsync(new RenameLedgerRequest {Id = 3, Name = "Unlocked"});

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Failed));
            Assert.That(outcome.Error, Is.EqualTo(ScryCommandNotFoundException.TargetMessage));
        });
    }

    [Test]
    public async Task AnUnknownCommandIsRejectedAsItIsOverHttp()
    {
        await using var server = await Server.Start(database);

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => server.Client.SendCommandAsync(new TeleportLedger()))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo(ScryErrorCode.Validation));
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task ADenialSurfacesAsItDoesOverHttp()
    {
        await using var server = await Server.Start(database, user: "mallory");

        Assert.ThrowsAsync<ScryPermissionException>(() => server.Client.SendCommandAsync(new RenameLedgerRequest {Id = 1, Name = "Mine"}));
    }

    // SignalR bounds a message's size and nothing about what it means; the command limit is the processor's.
    [Test]
    public async Task ACommandOverTheLimitIsRefusedAsItIsOverHttp()
    {
        await using var server = await Server.Start(database, _ => _.MaxCommandBytes = 64);

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => server.Client.SendCommandAsync(new OpenLedgerRequest {Name = new('x', 200)}))!;

        Assert.That(exception.Code, Is.EqualTo(ScryErrorCode.PayloadTooLarge));
    }

    [Test]
    public async Task TheLimitOnPendingCommandsAppliesOverTheHub()
    {
        await using var server = await Server.Start(
            database,
            _ =>
            {
                _.CommandSyncWindow = TimeSpan.Zero;
                _.MaxPendingCommands = 1;
            });
        var gate = server.Gate();
        server.Client.CommandWait = TimeSpan.Zero;
        var held = await server.Client.SendCommandAsync(new RenameLedgerRequest {Id = 1, Name = "Cash"});
        Assert.That(held.Status, Is.EqualTo(ScryCommandStatus.Pending));

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => server.Client.SendCommandAsync(new RenameLedgerRequest {Id = 2, Name = "Bank"}))!;
        gate.SetResult();

        Assert.That(exception.Code, Is.EqualTo(ScryErrorCode.CommandLimit));
    }

    [Test]
    public async Task TheCallerIsToldWhatItMaySend()
    {
        await using var alice = await Server.Start(database, user: "alice");
        await using var mallory = await Server.Start(database, user: "mallory");

        await alice.Client.Ready;
        await mallory.Client.Ready;

        Assert.Multiple(() =>
        {
            Assert.That(alice.Client.Can("RenameLedger"), Is.True);
            Assert.That(mallory.Client.Can("RenameLedger"), Is.False);
            Assert.That(mallory.Client.Can("OpenLedger"), Is.True);
        });
    }

    // A hub's methods are its type's, so a server with commands off has a method that says so.
    [Test]
    public async Task AServerNotServingCommandsSaysSo()
    {
        await using var server = await Server.Start(database, _ => _.MaxPendingCommands = 0);

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => server.Client.SendCommandAsync(new OpenLedgerRequest {Name = "x"}))!;
        await server.Client.Ready;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Body, Does.Contain(nameof(ScryOptions.MaxPendingCommands)));
            Assert.That(server.Client.Can("OpenLedger"), Is.False);
        });
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    // Fills the caller for each hub call, from the connection's user.
    sealed class CallerFilter :
        IHubFilter
    {
        public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocation, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            invocation.ServiceProvider.GetRequiredService<LedgerCaller>().Name = invocation.Context.User?.Identity?.Name;
            return next(invocation);
        }
    }

    sealed class Every(TimeSpan delay) :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            delay;
    }

    /// <summary>A server that maps the hub and nothing else, and a client connected to it as a user.</summary>
    sealed class Server(WebApplication app, HubConnection connection, LedgerGate gate) :
        IAsyncDisposable
    {
        public HubConnection Connection => connection;

        public ScryClient Client { get; } = ScrySignalRClient.Create(connection);

        public static async Task<Server> Start(
            SqlDatabase<LedgerContext> database,
            Action<ScryOptions>? configure = null,
            string? user = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSignalR(_ => _.AddFilter<CallerFilter>());
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
                configure?.Invoke(options);
            });
            builder.Services.AddScoped<ICommandHandler<RenameLedger>, RenameLedgerHandler>();
            builder.Services.AddScoped<ICommandHandler<OpenLedger, LedgerOpened>, OpenLedgerHandler>();
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, HeaderUserHandler>("Test", _ => { });

            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.MapScryHub("/hub");
                await app.StartAsync();

                var connection = new HubConnectionBuilder()
                    .WithUrl(
                        "http://localhost/hub",
                        options =>
                        {
                            options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                            options.Transports = HttpTransportType.LongPolling;
                            if (user is not null)
                            {
                                options.Headers.Add(HeaderUserHandler.Header, user);
                            }
                        })
                    .Build();
                await connection.StartAsync();
                return new(app, connection, gate);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        // Holds every rename open until the test completes what this returns.
        public TaskCompletionSource Gate()
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.Held = held;
            return held;
        }

        public async ValueTask DisposeAsync()
        {
            gate.Held?.TrySetResult();
            await Client.DisposeAsync();
            await connection.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

/// <summary>A command no server holds, as a client generated against another model would send it.</summary>
[ScryCommand("TeleportLedger")]
public sealed class TeleportLedger;
