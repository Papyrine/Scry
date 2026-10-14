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

        await serving.DrainAsync();
        var after = await serving.Reviews().ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(asked.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(answer).Contains("dr.osei");

            // Accepted before it was answered, and not moved on by the host that accepted it.
            await Assert.That(waiting).IsEqualTo(1);
            await Assert.That(before).IsEmpty();
            await Assert.That(after).Count().IsEqualTo(1);
            await Assert.That(after[0].Reviewer).IsEqualTo("auditor.lee");
            await Assert.That(after[0].Question).IsEqualTo(ScryDisclosureQuestion.Row);
            await Assert.That(after[0].Node).IsEqualTo("admin");
            await Assert.That(await serving.SegmentsAsync()).Count().IsEqualTo(1);
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

    static async Task<WebApplication> Reader(Action<IServiceCollection> register)
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/scry-disclosures/{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (reviewer is not null)
        {
            request.Headers.Add("X-Reviewer", reviewer);
        }

        return await client.SendAsync(request);
    }
}
