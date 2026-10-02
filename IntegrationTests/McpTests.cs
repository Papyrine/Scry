// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// Scry served to an agent over MCP, with nothing mapped but the MCP endpoint. What has to hold is that an
/// agent is a client like any other: the same answers, the same refusals, the same policies deciding them
/// from the same caller — and that nothing is served that the access level did not ask for.
/// </summary>
[NotInParallel]
public class McpTests
{
    static SqlDatabase<LedgerContext> database = null!;

    [Before(Class)]
    public static async Task BuildDatabase() =>
        database = await LedgerData.Instance.Build();

    [After(Class)]
    public static ValueTask DropDatabase() =>
        database.DisposeAsync();

    [Test]
    public async Task OffMapsNothing()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Off);

        var http = server.Http();
        using var response = await http.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ReadServesNothingThatWrites()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Read);
        await using var client = await server.Connect();

        var tools = await client.ListToolsAsync();
        var schema = ScryJson.DeserializeIntrospection(Text(await client.CallToolAsync("describe_schema")));

        using (Assert.Multiple())
        {
            await Assert.That(tools.Select(_ => _.Name)).IsEquivalentTo(["describe_schema", "query"]);
            await Assert.That(tools.All(_ => _.ProtocolTool.Annotations!.ReadOnlyHint == true)).IsTrue();
            await Assert.That(schema.Sources.Select(_ => _.Name)).Contains("Ledger");
            await Assert.That(schema.Commands).IsEmpty();
            await Assert.That(client.ServerInstructions).Contains("describe_schema");
            await Assert.That(client.ServerInstructions).DoesNotContain("send_command");
        }
    }

    [Test]
    public async Task ReadWriteServesCommands()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.ReadWrite);
        await using var client = await server.Connect();

        var tools = await client.ListToolsAsync();
        var schema = ScryJson.DeserializeIntrospection(Text(await client.CallToolAsync("describe_schema")));

        using (Assert.Multiple())
        {
            await Assert.That(tools.Select(_ => _.Name)).IsEquivalentTo(["describe_schema", "query", "send_command", "command_receipt", "capabilities"]);
            await Assert.That(tools.Single(_ => _.Name == "send_command").ProtocolTool.Annotations!.ReadOnlyHint).IsFalse();
            await Assert.That(schema.Commands.Select(_ => _.Name)).IsEquivalentTo(["OpenLedger", "RenameLedger"]);
            await Assert.That(client.ServerInstructions).Contains("send_command");
        }
    }

    [Test]
    public async Task AnAgentConnectsAndAsks()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Read);
        var httpClient = server.Http();

        // begin-snippet: mcpClientConnect
        var transport = new HttpClientTransport(
            new()
            {
                Endpoint = new("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);

        var schema = await client.CallToolAsync("describe_schema");
        var answer = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["root"] = "Ledger",
                ["pipeline"] = JsonDocument.Parse("""[{"$type":"count"}]""").RootElement
            });
        // end-snippet

        using (Assert.Multiple())
        {
            await Assert.That(schema.IsError).IsNotEqualTo(true);
            await Assert.That(ScryJson.DeserializeResponse(Text(answer)).Payload.GetInt32()).IsGreaterThanOrEqualTo(3);
        }
    }

    [Test]
    public async Task ReadWriteNeedsCommandsOn()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Server.Start(database, ScryMcpAccess.ReadWrite, _ => _.MaxPendingCommands = 0));

        await Assert.That(exception!.Message).Contains(nameof(ScryOptions.MaxPendingCommands));
    }

    [Test]
    public async Task AQueryIsAnsweredAsOverHttp()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Read);
        await using var client = await server.Connect();

        var result = await Query(
            client,
            "Ledger",
            """
            [
              {"$type":"where","predicate":{"$type":"binary","op":"LessThanOrEqual","left":{"$type":"member","path":"Id"},"right":{"$type":"const","value":"2","tag":"Int32"}}},
              {"$type":"orderBy","key":{"$type":"member","path":"Id"},"descending":false},
              {"$type":"select","projection":{"members":["Id"]}}
            ]
            """);
        var response = ScryJson.DeserializeResponse(Text(result));

        using (Assert.Multiple())
        {
            await Assert.That(result.IsError).IsNotEqualTo(true);
            await Assert.That(response.Kind).IsEqualTo(ResultKind.List);
            await Assert.That(response.Payload.EnumerateArray().Select(_ => _.GetProperty("id").GetInt32())).IsEquivalentTo([1, 2]);
        }
    }

    [Test]
    public async Task ATerminalIsAnswered()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Read);
        await using var client = await server.Connect();

        var result = await Query(
            client,
            "Ledger",
            """[{"$type":"any","predicate":{"$type":"member","path":"Locked"}}]""");
        var response = ScryJson.DeserializeResponse(Text(result));

        using (Assert.Multiple())
        {
            await Assert.That(response.Kind).IsEqualTo(ResultKind.Scalar);
            await Assert.That(response.Payload.GetBoolean()).IsTrue();
        }
    }

    // Read as strictly as a generated client's request: an unknown operator is not skipped.
    [Test]
    public async Task AMalformedQueryIsAWireFormatError()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Read);
        await using var client = await server.Connect();

        var result = await Query(client, "Ledger", """[{"$type":"drop"}]""");

        using (Assert.Multiple())
        {
            await Assert.That(result.IsError).IsTrue();
            await Assert.That(Error(result).Code).IsEqualTo(ScryErrorCode.WireFormat);
        }
    }

    // Named back to the agent, so it can correct itself.
    [Test]
    public async Task AMemberOffTheAllowListIsAValidationError()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Read);
        await using var client = await server.Connect();

        var result = await Query(client, "Ledger", """[{"$type":"where","predicate":{"$type":"member","path":"Secret"}}]""");
        var error = Error(result);

        using (Assert.Multiple())
        {
            await Assert.That(result.IsError).IsTrue();
            await Assert.That(error.Code).IsEqualTo(ScryErrorCode.Validation);
            await Assert.That(error.Error).Contains("Secret");
        }
    }

    [Test]
    public async Task ACommandIsAnsweredWithItsReceipt()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.ReadWrite);
        await using var client = await server.Connect();

        var result = await SendCommand(client, "OpenLedger", """{"name":"From an agent"}""");
        var receipt = ScryJson.DeserializeReceipt(Text(result));

        using (Assert.Multiple())
        {
            await Assert.That(receipt.Status).IsEqualTo(CommandStatus.Completed);
            await Assert.That(receipt.Result!.Value.GetProperty("id").GetInt32()).IsGreaterThan(3);
        }
    }

    [Test]
    public async Task APendingCommandIsAskedForAgain()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.ReadWrite, _ => _.CommandSyncWindow = TimeSpan.Zero);
        var gate = server.Gate();
        await using var client = await server.Connect();

        var pending = ScryJson.DeserializeReceipt(Text(await SendCommand(client, "RenameLedger", """{"id":1,"name":"Cash"}""")));
        await Assert.That(pending.Status).IsEqualTo(CommandStatus.Pending);

        gate.SetResult();
        var receipt = pending;
        var deadline = DateTime.UtcNow + patience;
        while (receipt.Status == CommandStatus.Pending &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            receipt = ScryJson.DeserializeReceipt(Text(await client.CallToolAsync("command_receipt", new Dictionary<string, object?> {["id"] = pending.Id.ToString()})));
        }

        await Assert.That(receipt.Status).IsEqualTo(CommandStatus.Completed);
    }

    // The policy reads the caller from the request's scope, which is the tool call's.
    [Test]
    public async Task ACommandPolicyDecidesByTheCaller()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.ReadWrite);
        await using var alice = await server.Connect("alice");
        await using var mallory = await server.Connect("mallory");

        var denied = await SendCommand(mallory, "RenameLedger", """{"id":2,"name":"Mine"}""");
        var aliceMay = ScryJson.DeserializeCapabilities(Text(await alice.CallToolAsync("capabilities")));
        var malloryMay = ScryJson.DeserializeCapabilities(Text(await mallory.CallToolAsync("capabilities")));

        using (Assert.Multiple())
        {
            await Assert.That(Error(denied).Code).IsEqualTo(ScryErrorCode.Forbidden);
            await Assert.That(aliceMay.Commands).Contains("RenameLedger");
            await Assert.That(malloryMay.Commands).DoesNotContain("RenameLedger");
        }
    }

    [Test]
    public async Task ARowTheCallerMayNotActOnIsNotFound()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.ReadWrite);
        await using var client = await server.Connect();

        var result = await SendCommand(client, "RenameLedger", """{"id":3,"name":"Unlocked"}""");

        await Assert.That(Error(result).Code).IsEqualTo(ScryErrorCode.NotFound);
    }

    [Test]
    public async Task AnUnknownCommandIsAValidationError()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.ReadWrite);
        await using var client = await server.Connect();

        var result = await SendCommand(client, "TeleportLedger", "{}");

        await Assert.That(Error(result).Code).IsEqualTo(ScryErrorCode.Validation);
    }

    [Test]
    public async Task AuthorizationGoesOnTheEndpoint()
    {
        await using var server = await Server.Start(database, ScryMcpAccess.Read, requireUser: true);

        await Assert.ThrowsAsync(() => server.Connect());
        await using var client = await server.Connect("alice");
        await Assert.That((await client.ListToolsAsync()).Count).IsEqualTo(2);
    }

    static Task<CallToolResult> Query(McpClient client, string root, string pipeline) =>
        client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["root"] = root,
                ["pipeline"] = JsonDocument.Parse(pipeline).RootElement
            }).AsTask();

    static Task<CallToolResult> SendCommand(McpClient client, string command, string payload) =>
        client.CallToolAsync(
            "send_command",
            new Dictionary<string, object?>
            {
                ["command"] = command,
                ["payload"] = JsonDocument.Parse(payload).RootElement
            }).AsTask();

    static string Text(CallToolResult result) =>
        ((TextContentBlock) result.Content.Single()).Text;

    static ScryError Error(CallToolResult result) =>
        ScryJson.TryDeserializeError(Text(result))!;

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    /// <summary>A server that maps the MCP endpoint and nothing else.</summary>
    sealed class Server(WebApplication app, LedgerGate gate) :
        IAsyncDisposable
    {
        public static async Task<Server> Start(
            SqlDatabase<LedgerContext> database,
            ScryMcpAccess access,
            Action<ScryOptions>? configure = null,
            bool requireUser = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var gate = new LedgerGate();
            var services = builder.Services;
            services.AddSingleton(gate);
            services.AddScoped<LedgerCaller>();
            services.AddDbContext<LedgerContext>(
                (services, options) => options
                    .UseSqlServer(database.ConnectionString)
                    .AddInterceptors(services.GetRequiredService<ScryChangeInterceptor>()));
            services.AddScry<LedgerContext>(options =>
            {
                options.AllowUnmappedSources = true;
                options.MaxPendingCommands = 100;
                options.Mcp = access;
                configure?.Invoke(options);
            });
            services.AddScryMcp();
            services.AddScoped<ICommandHandler<RenameLedger>, RenameLedgerHandler>();
            services.AddScoped<ICommandHandler<OpenLedger, LedgerOpened>, OpenLedgerHandler>();
            services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, HeaderUserHandler>("Test", _ => { });
            services.AddAuthorization();

            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.Use(LedgerCaller.FromRequest);
                var endpoints = app.MapScryMcp("/mcp");
                if (requireUser)
                {
                    endpoints.RequireAuthorization();
                }

                await app.StartAsync();
                return new(app, gate);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public HttpClient Http(string? user = null)
        {
            var client = app.GetTestClient();
            if (user is not null)
            {
                client.DefaultRequestHeaders.Add(HeaderUserHandler.Header, user);
            }

            return client;
        }

        // An agent connected as the named user.
        public Task<McpClient> Connect(string? user = null)
        {
            var transport = new HttpClientTransport(
                new()
                {
                    Endpoint = new("http://localhost/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                Http(user),
                ownsHttpClient: true);
            return McpClient.CreateAsync(transport);
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
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
