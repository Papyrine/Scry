using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

// RenameShift is deprecated in the test model, to exercise a deprecated command; its handler still
// has to name it.
#pragma warning disable CS0618

public partial class TypeScriptFixtureTests
{
    /// <summary>
    /// What the command endpoints answer, as it arrives: a command decided at once, one that failed,
    /// one refused, one whose target is not there, one that went pending and streamed its outcome, the
    /// same command asked for again by its id, an id nobody sent, and the capabilities read.
    /// </summary>
    /// <remarks>
    /// Each command is sent under an id fixed here, so a receipt's id is the request's and nothing is
    /// scrubbed but the schema stamp. A request's payload is written as the C# client writes one, through
    /// <see cref="ScryJson.Options"/>, with its members in the order introspection lists them: the server
    /// binds a payload by name, so the order is the writer's, and that one is the order every client
    /// reading the description has. The handlers write nothing, since the seeded database is shared.
    /// </remarks>
    [Test]
    public async Task CommandFramings()
    {
        var script = new FixtureCommandScript();
        var app = await CommandsHost(script);
        try
        {
            using var http = app.GetTestClient();

            var created = Command(
                "CreateShift",
                1,
                new
                {
                    day = new Date(2026, 3, 4),
                    name = "Late <shift> & \"night\" \u00e9",
                    perks = new List<Perks> {Perks.Parking | Perks.Gym, Perks.None}
                });
            var renamed = Command("RenameShift", 2, new {id = 1, name = "Night"});
            var refused = Command("CreateShift", 3, new {day = new Date(2026, 3, 4), name = "Night", perks = new List<Perks>(), colour = "red"});
            var absent = Command("SealContract", 4, new {contractId = 999});
            var pending = Command("RenameShift", 5, new {id = 1, name = "Slow"});

            var answeredAtOnce = await Send(http, "A command answered at once, with its result", HttpMethod.Post, "/api/query/command", created);

            script.Fail = "The rota is locked this week.";
            var failed = await Send(http, "A command that failed", HttpMethod.Post, "/api/query/command", renamed);
            script.Fail = null;

            Exchange[] exchanges =
            [
                answeredAtOnce,
                failed,
                await Send(http, "A command refused", HttpMethod.Post, "/api/query/command", refused),
                await Send(http, "A command whose target is not there", HttpMethod.Post, "/api/query/command", absent),
                await Pending(http, script, "A command going pending, then its outcome", pending),
                await Send(http, "A command asked for again by its id", HttpMethod.Get, $"/api/query/command/{Id(5)}", null),
                await Send(http, "An id nobody sent", HttpMethod.Get, $"/api/query/command/{Id(99)}", null),
                await Send(http, "What a caller may send", HttpMethod.Get, "/api/query/capabilities", null)
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
                .UseFileName("commands")
                .NotInline();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    static Guid Id(int value) =>
        new($"00000000-0000-0000-0000-{value:D12}");

    static string Command(string name, int id, object payload) =>
        Encoding.UTF8.GetString(ScryJson.SerializeToUtf8(CommandRequest.Create(name, Id(id), JsonSerializer.SerializeToElement(payload, ScryJson.Options))));

    static async Task<WebApplication> CommandsHost(FixtureCommandScript script)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => TestContext.CreateSeeded());
        builder.Services
            .AddSingleton(script)
            .AddScoped<ICommandHandler<Seal>, FixtureSealHandler>()
            .AddScoped<ICommandHandler<RenameShift>, FixtureRenameShiftHandler>()
            .AddScoped<ICommandHandler<CreateShift, ShiftCreated>, FixtureCreateShiftHandler>();
        builder.Services.AddScry<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AllowUnmappedSources = true;
            options.MaxPendingCommands = 10;
            options.CommandSyncWindow = TimeSpan.FromMilliseconds(200);
        });

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }

    // Held open past the sync window, so the server answers with a stream: the pending receipt is read
    // first, then the handler is let go, and the stream read to its end.
    static async Task<Exchange> Pending(HttpClient http, FixtureCommandScript script, string name, string body)
    {
        const string path = "/api/query/command";
        script.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = JsonBody(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        var released = false;
        while (await reader.ReadLineAsync(cancel.Token) is { } line)
        {
            text.Append(line);
            text.Append('\n');
            if (line.Length == 0 &&
                !released)
            {
                released = true;
                script.Gate.SetResult();
            }
        }

        script.Gate = null;
        return Exchange.From(name, HttpMethod.Post, path, body, response, Encoding.UTF8.GetBytes(text.ToString()));
    }
}

/// <summary>What the fixture's handlers do: hold one open, or fail one with a message to show.</summary>
sealed class FixtureCommandScript
{
    public TaskCompletionSource? Gate { get; set; }

    public string? Fail { get; set; }

    public async Task Run(Cancel cancel)
    {
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancel);
        }

        if (Fail is { } message)
        {
            throw new ScryCommandException(message);
        }
    }
}

sealed class FixtureSealHandler(FixtureCommandScript script) :
    ICommandHandler<Seal>
{
    public Task Handle(Seal command, ScryCommandContext context, Cancel cancel) =>
        script.Run(cancel);
}

sealed class FixtureRenameShiftHandler(FixtureCommandScript script) :
    ICommandHandler<RenameShift>
{
    public Task Handle(RenameShift command, ScryCommandContext context, Cancel cancel) =>
        script.Run(cancel);
}

sealed class FixtureCreateShiftHandler(FixtureCommandScript script) :
    ICommandHandler<CreateShift, ShiftCreated>
{
    public async Task<ShiftCreated> Handle(CreateShift command, ScryCommandContext context, Cancel cancel)
    {
        await script.Run(cancel);
        return new()
        {
            Id = 7
        };
    }
}
