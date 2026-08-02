/// <summary>
/// Launches the real Sample.Server — the same DLL <c>dotnet run</c> would execute — and a headless
/// Chromium, for fixtures that drive the live WebAssembly UI.
/// </summary>
/// <remarks>
/// One server and one browser per derived fixture rather than one shared across the assembly: a
/// second <c>[SetUpFixture]</c> in the global namespace would collide with <see cref="SharedScryServer"/>,
/// and a server start is seconds against a suite whose cost is dominated by the WASM boot on each
/// page load.
/// </remarks>
public abstract class BrowserFixture
{
    Process server = null!;
    IPlaywright playwright = null!;
    string workDir = null!;

    protected IBrowser Browser { get; private set; } = null!;

    /// <summary>The origin the sample server is listening on, with no trailing slash.</summary>
    protected string BaseUrl { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartServer()
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
        server.Start();

        await WaitForServer(port);

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

    [OneTimeTearDown]
    public async Task Stop()
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
            server.Kill(entireProcessTree: true);
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

    static string LocateServerDll()
    {
        // .../samples/Sample.Tests/bin/<config>/<tfm>/ — mirror <config>/<tfm> onto the server output.
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = baseDir.Name;
        var config = baseDir.Parent!.Name;

        var dir = baseDir;
        while (dir is not null &&
               !Directory.Exists(Path.Combine(dir.FullName, "Sample.Server")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException(
                "Could not locate the Sample.Server project from the test output directory.");
        }

        var dll = Path.Combine(dir.FullName, "Sample.Server", "bin", config, tfm, "Sample.Server.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("Sample.Server build output not found; build the sample first.", dll);
        }

        return dll;
    }

    static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static async Task WaitForServer(int port)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
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

        throw new TimeoutException($"Sample.Server did not start listening on port {port}.");
    }
}
