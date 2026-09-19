using Sample.Model;

/// <summary>
/// The NServiceBus sample, both halves, hosted here: a Scry server and a worker that are two processes
/// when run for real, joined only by a database and a transport. What is pinned is the sample's whole
/// point — a write the server never made and no interceptor of its could see reaches a live query it
/// holds, because the worker said what it saved.
/// </summary>
/// <remarks>
/// The server's live queries are configured never to poll, so an answer can only have arrived because
/// the change did. A LocalDB instance of its own, for the reason the F# tests have one: the projects
/// of this solution are tested in parallel, and two of them rebuilding one template at once deadlock.
/// </remarks>
[TestFixture]
public class NServiceBusSampleTests
{
    static SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        },
        storage: Storage.FromSuffix<SampleContext>("NServiceBus"));

    [Test]
    public async Task AWriteMadeByTheWorkerReachesALiveQueryOnTheServer()
    {
        await using var database = await sqlInstance.Build();
        var transport = Path.Combine(Path.GetTempPath(), $"scry-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transport);
        string[] shared = ["--database", database.ConnectionString, "--transport-storage", transport];

        await using var server = await NServiceBusServerHost.Build([.. shared, "--urls", "http://127.0.0.1:0"]);
        await server.StartAsync();
        using var worker = NServiceBusWorkerHost.Build(shared);
        await worker.StartAsync();
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new(server.Urls.First())
            };
            var query = new ScryQuery(ScryClient.ForHttp(http, "/api/query"));
            await using var answers = query.Order
                .Where(_ => _.Id == 1)
                .Select(_ => new {_.Amount})
                .Live()
                // ReSharper disable once MethodSupportsCancellation
                .GetAsyncEnumerator();

            Assert.That(await Next(answers), Is.True);
            var before = answers.Current.Single().Amount;

            // The server writes nothing here: it sends a command, and the worker saves.
            using var accepted = await http.PostAsync("/api/orders/1/reprice-via-worker", content: null);
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

            Assert.That(await Next(answers), Is.True);
            Assert.That(answers.Current.Single().Amount, Is.EqualTo(before + 1));
        }
        finally
        {
            await worker.StopAsync();
            await server.StopAsync();
            try
            {
                Directory.Delete(transport, recursive: true);
            }
            catch (IOException)
            {
                // The transport may still hold a file for a moment; the folder is a temporary one.
            }
        }
    }

    static Task<bool> Next<T>(IAsyncEnumerator<T> answers) =>
        answers.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
}
