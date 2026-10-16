/// <summary>
/// Launches the real Sample.WebServer — the same DLL <c>dotnet run</c> would execute — and a headless
/// Chromium, for fixtures that drive the live WebAssembly UI.
/// </summary>
/// <remarks>
/// One server and one browser per derived fixture rather than one shared across the assembly: a
/// server start is seconds against a suite whose cost is dominated by the WASM boot on each page load.
/// </remarks>
public abstract class BrowserFixture :
    BrowserTests
{
    /// <summary>
    /// The server and browser this fixture's tests share, started before its first test and stopped
    /// after its last.
    /// </summary>
    [ClassDataSource<BrowserHost>(Shared = SharedType.PerClass)]
    public required BrowserHost Host { get; init; }

    protected override BrowserHost Server => Host;
}

/// <summary>
/// What every fixture that drives a sample server through a browser shares, whichever server it is:
/// the pages a test opens, what they logged, and the check that the browser refused nothing under the
/// page's Content-Security-Policy.
/// </summary>
/// <remarks>
/// Apart from <see cref="BrowserFixture"/> because which server a fixture launches is said by the
/// type of the host it is handed, and that is an attribute's argument: it cannot be left open for a
/// derived fixture to fill in.
/// </remarks>
public abstract class BrowserTests
{
    /// <summary>The server and browser the fixture's tests share.</summary>
    protected abstract BrowserHost Server { get; }

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
    protected string BaseUrl => Server.BaseUrl;

    /// <summary>
    /// Opens a page, recording everything it logs for the duration of the test.
    /// </summary>
    /// <remarks>
    /// The only way a test gets a page — the browser itself is private — so no test can be written
    /// that quietly opts out of the recording.
    /// </remarks>
    protected async Task<IPage> NewPageAsync(BrowserNewPageOptions? options = null) =>
        Track(await Server.Browser.NewPageAsync(options));

    /// <summary>
    /// A context for pages that share an origin's storage — what two explorer windows in one browser
    /// do. A page opened on its own owns a context of its own, with a storage of its own, and
    /// Playwright refuses to open a second page in one of those.
    /// </summary>
    protected async Task<IBrowserContext> NewContextAsync()
    {
        var context = await Server.Browser.NewContextAsync();
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
        ReportConsoleOnFailure(Current);

        // After the reporting, which reads what the page logged.
        while (Current.Pages.TryTake(out var page))
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

        while (Current.Contexts.TryTake(out var context))
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

        await RefuseContentSecurityPolicyViolations(Current);
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

        await Assert.That(TakePolicyRefusals(test))
            .IsEmpty()
            .Because("The browser refused something under the explorer's Content-Security-Policy.");
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