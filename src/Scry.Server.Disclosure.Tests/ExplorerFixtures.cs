using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

/// <summary>
/// A host with the disclosure audit on and the explorer mapped over it, in this process and with no
/// port: what the explorer's tests ask their questions of.
/// </summary>
/// <remarks>
/// The record is the in-memory store, filled from batches made by hand, so that every time and every
/// name in an answer is the test's to choose. Who is reading is whoever the <c>X-Reviewer</c> header
/// says — a stand-in for a sign-in, good for a test and for nothing else.
/// </remarks>
sealed class ExplorerHost :
    IAsyncDisposable
{
    public static DateTimeOffset Start { get; } = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    WebApplication app = null!;

    public ScryMemoryDisclosureStore Store { get; } = new(new Ticking(Start.AddHours(8)));

    public HttpClient Client { get; private set; } = null!;

    /// <param name="configure">The explorer's options, after every way in has been opened.</param>
    /// <param name="sink">What the host records through where it is not the store itself.</param>
    /// <param name="environment">The environment the host says it is in, which the explorer's defaults go by.</param>
    /// <param name="defaults">Whether the explorer is left as a host that set nothing would have it.</param>
    /// <param name="audit">The audit's own settings.</param>
    /// <param name="services">Anything else the host registers.</param>
    public static async Task<ExplorerHost> Run(
        Action<ScryDisclosureExplorerOptions>? configure = null,
        Func<ScryMemoryDisclosureStore, IScryDisclosureSink>? sink = null,
        string environment = "Production",
        bool defaults = false,
        Action<ScryDisclosureOptions>? audit = null,
        Action<IServiceCollection>? services = null)
    {
        var host = new ExplorerHost();
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = environment
            });
        builder.WebHost.UseTestServer();
        var recorder = sink?.Invoke(host.Store) ?? host.Store;
        builder.Services.AddScry<ClinicContext>(
            _ => _.UseDisclosureAudit(
                recorder,
                settings =>
                {
                    // A review is timed by the audit's clock, so it ticks from a time of the test's own.
                    settings.Clock = new Ticking(Start.AddHours(4));
                    settings.Node = "ward-1";
                    settings.Unkeyed<Note>();
                    settings.Key<WardCensus>(_ => _.Ward);
                    audit?.Invoke(settings);
                }));

        // A sink that is not the store is not a reader either, so the store is registered as what
        // reads the record back, as a host with a store of its own would.
        builder.Services.AddSingleton<IScryDisclosureReader>(host.Store);
        builder.Services.AddSingleton<IScryDisclosureEraser>(host.Store);
        builder.Services.AddSingleton<IScryDisclosureStatus>(host.Store);
        services?.Invoke(builder.Services);
        host.app = builder.Build();
        host.app.MapScryDisclosureExplorer(
            options =>
            {
                options.Reviewer = Reviewer;
                if (!defaults)
                {
                    options.EnableGuard = _ => true;
                    options.EnableExport = _ => true;
                }

                configure?.Invoke(options);
            });
        await host.app.StartAsync();
        host.Client = host.app.GetTestClient();
        return host;
    }

    static string? Reviewer(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Reviewer", out var named))
        {
            return named.ToString();
        }

        return null;
    }

    public T Service<T>()
        where T : notnull =>
        app.Services.GetRequiredService<T>();

    /// <summary>A question asked as <paramref name="reviewer"/>, or as nobody.</summary>
    public async Task<HttpResponseMessage> Ask(string path, string? body = null, string? reviewer = "records.officer", string type = "application/json", string? site = null)
    {
        var method = HttpMethod.Get;
        if (body is not null)
        {
            method = HttpMethod.Post;
        }

        using var request = new HttpRequestMessage(method, $"/scry-disclosures/{path}");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, type);
        }

        if (reviewer is not null)
        {
            request.Headers.Add("X-Reviewer", reviewer);
        }

        if (site is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", site);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>The answer to a question, as text, having said it was answered.</summary>
    public async Task<string> Answer(string path, string? body = null, string? reviewer = "records.officer")
    {
        using var response = await Ask(path, body, reviewer);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new($"{path} answered {(int) response.StatusCode}: {text}");
        }

        return text;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.DisposeAsync();
    }

    /// <summary>
    /// The clinic's morning, recorded by hand: two answers to a doctor, one to a nurse that carried a
    /// diagnosis, and one whose close says its last row never left.
    /// </summary>
    public async Task<Morning> Recorded()
    {
        var names = Clinic.Shape(
            new ScryDisclosureField("Patient", "Name", ScryDisclosureFieldUse.Returned, false),
            new ScryDisclosureField("Patient", "Name", ScryDisclosureFieldUse.Read, false));
        var charts = Clinic.Shape(
            new ScryDisclosureField("Patient", "Name", ScryDisclosureFieldUse.Returned, false),
            new ScryDisclosureField("Patient", "Diagnosis", ScryDisclosureFieldUse.Returned, true),
            new ScryDisclosureField("Patient", "Ward", ScryDisclosureFieldUse.Traversed, false),
            new ScryDisclosureField("Ward", "Name", ScryDisclosureFieldUse.Returned, false));
        var asked = Clinic.Content(ScryDisclosureContentKind.Request, "{\"version\":1,\"root\":\"Patient\",\"pipeline\":[]}");
        var ada = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Ada\"}");
        var brook = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Brook\"}");
        var chidi = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Chidi\"}");
        var chart = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Ada\",\"diagnosis\":\"Fracture\",\"ward\":\"North\"}");

        var first = Begin(1, "dr.osei", names, asked);
        var second = Begin(2, "nurse.kim", charts, asked, sensitive: true);
        var third = Begin(3, "dr.osei", names, asked, kind: ScryDisclosureKind.Stream);
        ScryDisclosureBatch[] batches =
        [
            With(Clinic.Part(first.Id, 0, first, names, Close(ScryDisclosureOutcome.Released, 3), (0, ada, "Patient", "[1]"), (1, brook, "Patient", "[2]"), (2, chidi, "Patient", "[3]")), asked),
            With(Reached(Clinic.Part(second.Id, 0, second, charts, Close(ScryDisclosureOutcome.Released, 1), (0, chart, "Patient", "[1]")), "Ward", "[1]", "Ward"), asked),
            With(Clinic.Part(third.Id, 0, third, names, Close(ScryDisclosureOutcome.Truncated, 1), (0, ada, "Patient", "[1]"), (1, brook, "Patient", "[2]")), asked)
        ];
        foreach (var batch in batches)
        {
            await Store.AppendAsync(batch, Cancel.None);
        }

        return new(first.Id, second.Id, third.Id);
    }

    public static ScryDisclosureEvent Begin(
        int minute,
        string? caller,
        ScryDisclosureShape? shape = null,
        ScryDisclosureContent? request = null,
        bool sensitive = false,
        ScryDisclosureKind kind = ScryDisclosureKind.List,
        string source = "Patient") =>
        new(Guid.CreateVersion7(), Start.AddMinutes(minute), kind, source)
        {
            Caller = caller,
            Shape = shape?.Address,
            Request = request?.Address,
            Sensitive = sensitive,
            Node = "ward-1",
            Stamp = "stamp-1"
        };

    public static ScryDisclosureClose Close(ScryDisclosureOutcome outcome, int units) =>
        new(outcome, units)
        {
            At = Start.AddHours(1)
        };

    // A batch with a piece of content beside what its units carried: the request it answered.
    public static ScryDisclosureBatch With(ScryDisclosureBatch batch, ScryDisclosureContent content) =>
        new()
        {
            EventId = batch.EventId,
            Sequence = batch.Sequence,
            Begin = batch.Begin,
            Shape = batch.Shape,
            Units = batch.Units,
            Entities = batch.Entities,
            Contents = [.. batch.Contents, content],
            Close = batch.Close
        };

    // A batch whose first unit also reached another row, through a navigation.
    static ScryDisclosureBatch Reached(ScryDisclosureBatch batch, string source, string key, string via) =>
        new()
        {
            EventId = batch.EventId,
            Sequence = batch.Sequence,
            Begin = batch.Begin,
            Shape = batch.Shape,
            Units = batch.Units,
            Entities = [.. batch.Entities, new(0, 1, source, key, via)],
            Contents = batch.Contents,
            Close = batch.Close
        };
}

/// <summary>The ids of what <see cref="ExplorerHost.Recorded"/> recorded.</summary>
sealed record Morning(Guid Names, Guid Chart, Guid Cut);

// A clock that says a second later each time it is asked, from a time of the test's own: every
// review is then timed, in order, without any of them being when the test happened to run.
sealed class Ticking(DateTimeOffset start) :
    TimeProvider
{
    long asked;

    public override DateTimeOffset GetUtcNow() =>
        start.AddSeconds(Interlocked.Increment(ref asked));
}
