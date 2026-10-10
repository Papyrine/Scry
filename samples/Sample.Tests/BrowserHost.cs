/// <summary>
/// A real sample server, launched as its own process, and a headless Chromium to drive it: one pair
/// per browser fixture. Sample.WebServer, unless a fixture's host says another.
/// </summary>
public class BrowserHost :
    TUnit.Core.Interfaces.IAsyncInitializer,
    IAsyncDisposable
{
    // Numbers each launch, to name its database: the fixtures run at once, and two servers building
    // one name knock each other's database offline. Counted rather than random so the names a run
    // leaves behind are the same few every run.
    static int launches;

    // Each server clones its database from one shared LocalDB template, which the first to start builds
    // before it listens. Two building it at once deadlock: one CREATE DATABASE is the victim, and the
    // other is then left a template file it cannot open, so every server after fails to start. The
    // first launch therefore runs alone, and the rest start once it is listening — by which point the
    // template is there to clone. A first launch that fails leaves the template unmarked, so the next
    // launch runs alone in its place. One of these for each server project, since each has a LocalDB
    // instance, and so a template, of its own.
    sealed class Template
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Built;
    }

    static ConcurrentDictionary<string, Template> templates = new();

    /// <summary>The sample project whose server this launches.</summary>
    protected virtual string Project => "Sample.WebServer";

    /// <summary>Anything more the server is to be told through its environment.</summary>
    protected virtual void Configure(IDictionary<string, string?> environment)
    {
    }

    Process server = null!;
    IPlaywright playwright = null!;
    string workDir = null!;

    /// <summary>The origin the sample server is listening on, with no trailing slash.</summary>
    public string BaseUrl { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        // Run the server from a throwaway working directory so nothing it writes lands in the repo.
        workDir = Directory.CreateTempSubdirectory("scry_ui_").FullName;

        server = new()
        {
            StartInfo =
            {
                FileName = "dotnet",
                WorkingDirectory = workDir,
                UseShellExecute = false
            }
        };
        server.StartInfo.ArgumentList.Add(LocateServerDll());
        server.StartInfo.Environment["ASPNETCORE_URLS"] = BaseUrl;
        // Development so the (Development-only) Scry explorer is reachable; the server's explicit
        // UseStaticWebAssets() call means the WASM client is served in this environment too.
        server.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        server.StartInfo.Environment["SAMPLE_DATABASE"] = $"Browser{Interlocked.Increment(ref launches)}";
        Configure(server.StartInfo.Environment);

        var template = templates.GetOrAdd(Project, _ => new());
        if (Volatile.Read(ref template.Built))
        {
            await StartServer(port, TimeSpan.FromSeconds(30));
        }
        else
        {
            await template.Gate.WaitAsync();
            try
            {
                // Building the template from cold is most of a launch's time, so the one that may have
                // to is given longer.
                await StartServer(port, TimeSpan.FromMinutes(2));
                Volatile.Write(ref template.Built, true);
            }
            finally
            {
                template.Gate.Release();
            }
        }

        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync(
            new()
            {
                // Grayscale text rather than Chromium's default LCD subpixel antialiasing. The colour
                // fringing it produces is not stable between browser sessions — the same page, on the
                // same machine, rasterises one element with different fringing from one run of the
                // suite to another — which is invisible to a reader and fatal to a screenshot
                // comparison. Turning it off costs these captures nothing: nobody reads them for
                // subpixel fidelity, and it is what makes a committed baseline reproducible.
                Args = ["--disable-lcd-text"]
            });
    }

    public async ValueTask DisposeAsync()
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        playwright?.Dispose();

        if (server is {HasExited: false})
        {
            // The server alone, and not what it started. LocalDB runs an instance's engine as a
            // child of whichever process first asked for it, which is the first server launched:
            // ending that server's tree ends the engine under every other server still using it.
            server.Kill();
            // Give the process a moment to exit before the working directory is removed.
            server.WaitForExit(milliseconds: 5000);
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        server?.Dispose();

        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup of a temp directory; a lingering file lock is not a test failure.
        }
    }

    string LocateServerDll()
    {
        // .../samples/Sample.Tests/bin/<config>/<tfm>/ — mirror <config>/<tfm> onto the server output.
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = baseDir.Name;
        var config = baseDir.Parent!.Name;

        var dll = Path.GetFullPath(
            Path.Combine(ProjectFiles.SolutionDirectory, Project, "bin", config, tfm, $"{Project}.dll"));
        if (File.Exists(dll))
        {
            return dll;
        }

        throw new FileNotFoundException($"{Project} build output not found; build the sample first.", dll);
    }

    static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    async Task StartServer(int port, TimeSpan timeout)
    {
        server.Start();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // A server that has exited will never listen: say so now, rather than when the wait runs out.
            if (server.HasExited)
            {
                throw new InvalidOperationException($"{Project} exited with code {server.ExitCode} before listening on port {port}.");
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"{Project} did not start listening on port {port} within {timeout}.");
    }
}