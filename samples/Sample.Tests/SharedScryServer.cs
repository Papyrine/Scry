/// <summary>
/// A single in-process <see cref="ScryTestServer"/> shared by the in-process fixtures
/// (<c>IndexPageTests</c>, <c>WireFormatTests</c>). Those tests only read, so cloning one seeded
/// database and starting one <c>TestServer</c> for all of them — rather than one per test — avoids
/// repeating the (~4s) build/clone/start on every test. Started on first use so a <c>Browser</c>-only
/// run, which never touches it, pays nothing.
/// </summary>
[SetUpFixture]
public class SharedScryServer
{
    static ScryTestServer? server;

    public static async Task<ScryTestServer> InstanceAsync() =>
        // The in-process fixtures are not parallelised, so first-use construction is never concurrent.
        server ??= await ScryTestServer.StartAsync();

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
            server = null;
        }
    }
}
