/// <summary>
/// Launches the real Sample.WebServer — the same DLL <c>dotnet run</c> would execute — and a headless
/// Chromium, for fixtures that drive the live WebAssembly UI.
/// </summary>
/// <remarks>
/// One server and one browser per derived fixture rather than one shared across the assembly: a
/// server start is seconds against a suite whose cost is dominated by the WASM boot on each page load.
/// </remarks>
public abstract class BrowserFixture
{
    /// <summary>
    /// The server and browser this fixture's tests share, started before its first test and stopped
    /// after its last.
    /// </summary>
    [ClassDataSource<BrowserHost>(Shared = SharedType.PerClass)]
    public required BrowserHost Host { get; init; }

    /// <summary>
    /// What one running test opened and what it logged.
    /// </summary>
    /// <remarks>
    /// Held per test: each test runs on an instance of its own, so this is only ever the one test's.
    /// </remarks>
    sealed class RunningTest
    {
        // What the page logged during the test. Written from Playwright's own threads, so a concurrent
        // collection rather than a List.
        public ConcurrentQueue<string> Console { get; } = new();

        // Every page the test opened. A page holds a fully booted WASM runtime — Roslyn included,
        // untrimmed and interpreted, because the explorer needs the interpreter — and the browser keeps
        // one alive until it is closed. Left open, a suite this size walks the agent out of memory: the
        // failure lands as "MONO_WASM: sbrk failed to allocate", tens of tests after the one that spent
        // the memory, and everything after it fails to boot at all. A bag rather than a field because a
        // test may open a second page (a shared link opens one).
        public ConcurrentBag<IPage> Pages { get; } = [];

        // The contexts opened for pages that share storage; closed after their pages.
        public ConcurrentBag<IBrowserContext> Contexts { get; } = [];
    }

    RunningTest Current { get; } = new();

    /// <summary>The origin the sample server is listening on, with no trailing slash.</summary>
    protected string BaseUrl => Host.BaseUrl;

    /// <summary>
    /// Opens a page, recording everything it logs for the duration of the test.
    /// </summary>
    /// <remarks>
    /// The only way a test gets a page — the browser itself is private — so no test can be written
    /// that quietly opts out of the recording.
    /// </remarks>
    protected async Task<IPage> NewPageAsync(BrowserNewPageOptions? options = null) =>
        Track(await Host.Browser.NewPageAsync(options));

    /// <summary>
    /// A context for pages that share an origin's storage — what two explorer windows in one browser
    /// do. A page opened on its own owns a context of its own, with a storage of its own, and
    /// Playwright refuses to open a second page in one of those.
    /// </summary>
    protected async Task<IBrowserContext> NewContextAsync()
    {
        var context = await Host.Browser.NewContextAsync();
        Current.Contexts.Add(context);
        return context;
    }

    protected async Task<IPage> NewPageInAsync(IBrowserContext context) =>
        Track(await context.NewPageAsync());

    IPage Track(IPage page)
    {
        // Resolved once, here, rather than inside the handlers, which run on Playwright's threads.
        var test = Current;
        test.Pages.Add(page);
        page.Console += (_, message) => test.Console.Enqueue($"[{message.Type}] {message.Text}");
        page.PageError += (_, error) => test.Console.Enqueue($"[pageerror] {error}");
        return page;
    }

    /// <summary>
    /// Reports what the page logged, but only for a test that failed.
    /// </summary>
    /// <remarks>
    /// A failure here is almost always the UI never reaching the state the test waits for, and what
    /// Playwright can say about that is only that a selector never appeared — a bare 30 second timeout,
    /// identical whatever the cause. The page usually knows exactly what went wrong and says so in its
    /// console: a boot that failed an integrity check because the embedded assets went stale, an
    /// exception out of the in-browser Roslyn, a 404 for an asset. None of that reached the test output
    /// before, which made a whole suite of timeouts say nothing about which of those it was.
    /// </remarks>
    [After(Test)]
    public async Task EndTest()
    {
        var test = Current;

        ReportConsoleOnFailure(test);

        // After the reporting, which reads what the page logged.
        while (test.Pages.TryTake(out var page))
        {
            try
            {
                await page.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // A page the test already closed, or one whose browser is going away. Neither is a
                // failure of the test that just ran.
            }
        }

        while (test.Contexts.TryTake(out var context))
        {
            try
            {
                await context.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // As above.
            }
        }

        await RefuseContentSecurityPolicyViolations(test);
    }

    /// <summary>
    /// Fails a test that otherwise passed where the browser refused something under the explorer's
    /// <c>Content-Security-Policy</c>. The browser says so only as a console error, so every browser
    /// test doubles as the check that the policy allows what the explorer actually does: a directive
    /// tightened past what Monaco or the runtime needs fails here naming the refusal, rather than as a
    /// page that quietly stopped completing or booting somewhere else in the suite.
    /// </summary>
    static async Task RefuseContentSecurityPolicyViolations(RunningTest test)
    {
        if (Failed())
        {
            return;
        }

        await Assert.That(TakePolicyRefusals(test)).IsEmpty().Because("The browser refused something under the explorer's Content-Security-Policy.");
    }

    /// <summary>
    /// The policy refusals the page logged so far, removed from the record. For the one test that
    /// provokes a refusal on purpose, to prove the record sees them — taking them is what keeps that
    /// test from failing its own tear-down.
    /// </summary>
    protected IReadOnlyList<string> TakePolicyRefusals() =>
        TakePolicyRefusals(Current);

    static IReadOnlyList<string> TakePolicyRefusals(RunningTest test)
    {
        var refused = new List<string>();
        var kept = new List<string>();
        while (test.Console.TryDequeue(out var message))
        {
            if (message.Contains("Content Security Policy", StringComparison.Ordinal))
            {
                refused.Add(message);
            }
            else
            {
                kept.Add(message);
            }
        }

        foreach (var message in kept)
        {
            test.Console.Enqueue(message);
        }

        return refused;
    }

    static void ReportConsoleOnFailure(RunningTest test)
    {
        if (!Failed() ||
            test.Console.IsEmpty)
        {
            return;
        }

        var context = TestContext.Current!;
        context.Output.WriteLine($"Browser console during {context.Metadata.TestName}:");
        foreach (var message in test.Console)
        {
            context.Output.WriteLine($"  {message}");
        }
    }

    static bool Failed() =>
        TestContext.Current?.Execution.Result?.State == TestState.Failed;
}

/// <summary>
/// The real Sample.WebServer, launched as its own process, and a headless Chromium to drive it: one
/// pair per browser fixture.
/// </summary>
public sealed class BrowserHost :
    TUnit.Core.Interfaces.IAsyncInitializer,
    IAsyncDisposable
{
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
               !Directory.Exists(Path.Combine(dir.FullName, "Sample.WebServer")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException(
                "Could not locate the Sample.WebServer project from the test output directory.");
        }

        var dll = Path.Combine(dir.FullName, "Sample.WebServer", "bin", config, tfm, "Sample.WebServer.dll");
        if (File.Exists(dll))
        {
            return dll;
        }

        throw new FileNotFoundException("Sample.WebServer build output not found; build the sample first.", dll);
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

        throw new TimeoutException($"Sample.WebServer did not start listening on port {port}.");
    }
}
