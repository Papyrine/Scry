using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using static SqlStores;

/// <summary>
/// The disclosure explorer on a host that serves no queries: an admin application with no model and
/// no <c>AddScry</c>, apart from the servers that answer, reading the record they keep. What it is
/// asked is recorded as on any other host, and the record is moved on by the serving nodes alone.
/// </summary>
public class ExplorerReaderHostTests
{
    // A serving node records an answer and moves it on. The reader host answers a question about it,
    // having recorded the question first, and leaves that record in the outbox for the serving node
    // to move, which is when the question is in the record.
    [Test]
    public async Task AHostThatOnlyReadsTheRecordAnswersFromIt()
    {
        await using var database = await Clinic.Instance.Build();
        await using var serving = Store(database);
        var processor = Processor(serving, new());
        processor.Execute(Clinic.Names(), database.Context);
        await serving.DrainAsync();

        await using var app = await Reader(_ => _.AddSqlServerDisclosureRecord(database.ConnectionString, configure: _ => _.Node = "admin"));
        using var client = app.GetTestClient();
        using var asked = await Ask(client, "api/rows", """{"source":"Patient","key":"1"}""");
        var answer = await asked.Content.ReadAsStringAsync();
        var waiting = (await serving.Status()).Pending;
        var before = await serving.Reviews().ToListAsync();
        using var standing = await Ask(client, "api/status", "");
        var stands = await standing.Content.ReadAsStringAsync();

        await serving.DrainAsync();
        var after = await serving.Reviews().ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(asked.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(answer).Contains("dr.osei");

            // Accepted before it was answered, and not moved on by the host that accepted it.
            await Assert.That(waiting).IsEqualTo(1);
            await Assert.That(before).IsEmpty();
            // The question about the row, and the look at how the record stands.
            await Assert.That(after.Select(_ => _.Question)).IsEquivalentTo([ScryDisclosureQuestion.Row, ScryDisclosureQuestion.Status], CollectionOrdering.Any);
            await Assert.That(after.All(_ => _ is {Reviewer: "auditor.lee", Node: "admin"})).IsTrue();
            await Assert.That(await serving.SegmentsAsync()).Count().IsEqualTo(1);

            // The record says how it is kept, and the explorer shows it.
            await Assert.That(stands).Contains("\"segments\":[{\"number\":1");
            await Assert.That(stands).Contains("\"retention\":{");
        }
    }

    // Any store will do: the registration takes the sink the serving nodes record to, and whichever
    // of reader, eraser and status it also is.
    [Test]
    public async Task AnyStoreCanBeReadFromAHostOfItsOwn()
    {
        var store = new ScryMemoryDisclosureStore();
        await store.AppendAsync(Clinic.Batch(1), Cancel.None);

        await using var app = await Reader(_ => _.AddScryDisclosureRecord(store));
        using var client = app.GetTestClient();
        using var asked = await Ask(client, "api/callers", """{"caller":"dr.osei"}""");
        var reviews = await store.Reviews().ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(asked.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(reviews).Count().IsEqualTo(1);
            await Assert.That(reviews[0].Question).IsEqualTo(ScryDisclosureQuestion.Caller);
        }
    }

    // An export says what asked on the caller's behalf, and the version each row was at, where the
    // record has them.
    [Test]
    public async Task AnExportSaysWhatAskedAndWhichVersion()
    {
        var store = new ScryMemoryDisclosureStore();
        var id = Guid.CreateVersion7();
        var row = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Ada\"}");
        var batch = Clinic.Part(
            id,
            0,
            new(id, DateTimeOffset.UtcNow, ScryDisclosureKind.List, "Patient")
            {
                Caller = "dr.osei",
                Agent = "ward-assistant"
            },
            close: new(ScryDisclosureOutcome.Released, 1),
            rows: [(0, row, "Patient", "[1]")]);
        batch = new()
        {
            EventId = batch.EventId,
            Begin = batch.Begin,
            Units = batch.Units,
            Entities = [batch.Entities[0] with {Version = "7"}],
            Contents = batch.Contents,
            Close = batch.Close
        };
        await store.AppendAsync(batch, Cancel.None);

        await using var app = await Reader(_ => _.AddScryDisclosureRecord(store), export: true);
        using var client = app.GetTestClient();
        using var csv = await Ask(client, "api/export", $$"""{"format":"csv","event":"{{id:D}}"}""");
        using var json = await Ask(client, "api/export", $$"""{"format":"json","event":"{{id:D}}"}""");
        var csvText = await csv.Content.ReadAsStringAsync();
        var jsonText = await json.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(csvText).Contains("When,Caller,Agent,Kind");
            await Assert.That(csvText).Contains("dr.osei,ward-assistant,List");
            await Assert.That(csvText).Contains("Patient[1] at version 7");
            await Assert.That(jsonText).Contains("\"agent\": \"ward-assistant\"");
            await Assert.That(jsonText).Contains("Patient[1] at version 7");
        }
    }

    // Nobody named, on a host whose settings do not allow it, is refused as on a serving host: the
    // settings are the ones the record was registered with.
    [Test]
    public async Task AReaderHostKeepsTheAuditsRuleOnNamingTheReader()
    {
        var store = new ScryMemoryDisclosureStore();
        await using var app = await Reader(_ => _.AddScryDisclosureRecord(store));
        using var client = app.GetTestClient();
        using var asked = await Ask(client, "api/callers", """{"caller":"dr.osei"}""", reviewer: null);

        using (Assert.Multiple())
        {
            await Assert.That(asked.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(await store.Reviews().ToListAsync()).IsEmpty();
        }
    }

    // A host with no record registered is told so at startup, with both ways to register one.
    [Test]
    public async Task AHostWithNoRecordIsToldHowToRegisterOne()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Reader(_ => { }));
        using (Assert.Multiple())
        {
            await Assert.That(thrown!.Message).Contains("AddSqlServerDisclosureRecord");
            await Assert.That(thrown.Message).Contains("UseDisclosureAudit");
        }
    }

    // Starting segments and retiring them is the serving nodes', so a reader host refuses to be told
    // how, rather than ignoring it.
    [Test]
    public async Task AReaderHostRefusesTheSettingsThatMoveTheRecord()
    {
        var services = new ServiceCollection();
        var thrown = Assert.Throws<ArgumentException>(() => services.AddSqlServerDisclosureRecord("Server=.", _ => _.Keep = 12));
        await Assert.That(thrown!.Message).Contains("serving nodes");
    }

    static async Task<WebApplication> Reader(Action<IServiceCollection> register, bool export = false)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = "Production"
            });
        builder.WebHost.UseTestServer();
        register(builder.Services);
        var app = builder.Build();
        try
        {
            app.MapScryDisclosureExplorer(
                _ =>
                {
                    _.EnableGuard = _ => true;
                    _.EnableExport = _ => export;
                    _.Reviewer = context => context.Request.Headers["X-Reviewer"].FirstOrDefault();
                });
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    static async Task<HttpResponseMessage> Ask(HttpClient client, string path, string body, string? reviewer = "auditor.lee")
    {
        // Asked for with no body, as the status is; posted with one, as a question is.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/scry-disclosures/{path}");
        if (body.Length > 0)
        {
            request.Method = HttpMethod.Post;
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (reviewer is not null)
        {
            request.Headers.Add("X-Reviewer", reviewer);
        }

        return await client.SendAsync(request);
    }
}
