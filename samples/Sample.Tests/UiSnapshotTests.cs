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
        // The server calls UseStaticWebAssets(), so the WASM client is served outside Development too.
        server.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
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
        await Assertions.Expect(page.Locator("table")).ToHaveCountAsync(2);

        await Verify(page);
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
