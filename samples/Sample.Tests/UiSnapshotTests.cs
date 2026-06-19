using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace Sample.Tests;

// Launches the real Sample.Server (the same DLL `dotnet run` would execute) and drives it with a
// headless Chromium browser, snapshotting the live WebAssembly UI as HTML + a screenshot.
// Categorised "Browser" so CI can opt out (screenshots are environment sensitive, and the browser
// download / WASM boot is heavier than the in-process tests).
[TestFixture]
[Category("Browser")]
public class UiSnapshotTests
{
    Process server = null!;
    IPlaywright playwright = null!;
    IBrowser browser = null!;
    string baseUrl = null!;
    string workDir = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        var port = GetFreePort();
        baseUrl = $"http://127.0.0.1:{port}";

        // Run from a throwaway directory so the server's SQLite file does not land in the repo.
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
        server.StartInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        // Development so the (Development-only) Scry explorer is reachable; the server's explicit
        // UseStaticWebAssets() call means the WASM client is served in this environment too.
        server.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        server.Start();

        await WaitForServer(port);

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();

        if (server is { HasExited: false })
        {
            server.Kill(entireProcessTree: true);
            // Let the process release its SQLite file before the directory is removed.
            server.WaitForExit(milliseconds: 5000);
        }

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

    [Test]
    public async Task HomePage()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync(baseUrl);

        // Wait until the WebAssembly app has run its queries and rendered both result tables.
        await page.WaitForSelectorAsync("table tbody tr");
        await Assertions.Expect(page.Locator("table")).ToHaveCountAsync(3);

        // Snapshot the real WebAssembly-rendered DOM (not a screenshot): pixel screenshots differ
        // across machines/OS font rendering and can't run in CI, but the rendered markup is stable.
        var rendered = await page.Locator("#app").InnerHTMLAsync();
        await Verify(rendered);
    }

    // The explorer fetches GET {route}/introspect on load to learn the queryable schema. Exercises the
    // endpoint end-to-end (routing, Development guard, ScryJson serialization) against the live server.
    [Test]
    public async Task ExplorerIntrospectionEndpoint()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"{baseUrl}/scry/introspect");

        await Verify(json);
    }

    // Verifies the Scry explorer (a separate Blazor WASM app, embedded in and served by the
    // Scry.Server.Explorer package under /scry) boots from the embedded assets in a real browser.
    [Test]
    public async Task ExplorerBoots()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");

        // Allow the WASM runtime to download and boot before asserting the app rendered.
        await page.WaitForSelectorAsync("[data-testid='explorer-title']", new() { Timeout = 30_000 });
        var title = page.Locator("[data-testid='explorer-title']");
        await Assertions.Expect(title).ToHaveTextAsync("Scry Explorer");

        // The Monaco editor mounts only if the embedded _content/BlazorMonaco assets are served.
        await page.WaitForSelectorAsync(".monaco-editor", new() { Timeout = 30_000 });
    }

    // Proves Roslyn runs in the browser and completes against the introspected schema: the explorer
    // auto-runs completion for "Query.Employee.Where(e => e." on load and should offer Employee members.
    [Test]
    public async Task ExplorerCompletion()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");

        // Roslyn init + first completion in the WASM interpreter is slow on a cold load.
        await page.WaitForSelectorAsync("[data-testid='completions'] li", new() { Timeout = 90_000 });
        var items = await page.Locator("[data-testid='completions'] li").AllInnerTextsAsync();

        Assert.That(items, Does.Contain("Active"));
        Assert.That(items, Does.Contain("Name"));
        Assert.That(items, Does.Contain("Status"));
        Assert.That(items, Does.Contain("Manager"));
    }

    // Proves the inline Monaco IntelliSense dropdown is wired to the Roslyn provider.
    [Test]
    public async Task ExplorerInlineSuggestions()
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{baseUrl}/scry");
        await page.WaitForSelectorAsync(".monaco-editor", new() { Timeout = 30_000 });
        // Wait for the schema to load (provider registered) — the auto-run completion list appears.
        await page.WaitForSelectorAsync("[data-testid='completions'] li", new() { Timeout = 90_000 });

        // Place the caret at the end (after "e.") and trigger IntelliSense via Monaco's API.
        await page.EvaluateAsync(
            "() => { const e = monaco.editor.getEditors()[0]; e.focus(); const m = e.getModel();" +
            " e.setPosition({ lineNumber: 1, column: m.getLineMaxColumn(1) });" +
            " e.trigger('test', 'editor.action.triggerSuggest', {}); }");

        await page.WaitForSelectorAsync(".suggest-widget .monaco-list-row", new() { Timeout = 30_000 });
        var rows = await page.Locator(".suggest-widget .monaco-list-row").AllInnerTextsAsync();
        Assert.That(rows.Any(_ => _.Contains("Active")), Is.True, $"suggest rows: {string.Join(" | ", rows)}");
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
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
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
