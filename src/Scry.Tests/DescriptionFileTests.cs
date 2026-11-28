using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The description a host writes for a client generated at build time: the document the explorer
/// serves, written where the host says, in Development unless told otherwise, and left alone while
/// it has not changed.
/// </summary>
public class DescriptionFileTests
{
    [Test]
    public async Task TheDescriptionIsWhatTheProcessorDescribes()
    {
        using var root = new TemporaryDirectory();
        await using var app = await Host(root.Path, Environments.Development);

        app.WriteScryDescription("client/scry.description.json");

        var written = await File.ReadAllTextAsync(Path.Combine(root.Path, "client", "scry.description.json"));
        var processor = app.Services.GetRequiredService<ScryProcessor>();
        using (Assert.Multiple())
        {
            await Assert.That(written).IsEqualTo(ScryJson.Serialize(processor.Describe()));
            await Assert.That(Directory.GetFiles(Path.Combine(root.Path, "client"))).Count().IsEqualTo(1);
        }
    }

    // A host started again over the same model leaves the file, and the watcher on it, alone.
    [Test]
    public async Task AnUnchangedDescriptionIsNotWrittenAgain()
    {
        using var root = new TemporaryDirectory();
        await using var app = await Host(root.Path, Environments.Development);
        var path = Path.Combine(root.Path, "scry.description.json");
        app.WriteScryDescription(path);
        var before = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, before);

        app.WriteScryDescription(path);

        await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(before);
    }

    [Test]
    public async Task AChangedDescriptionIsWrittenOverTheOld()
    {
        using var root = new TemporaryDirectory();
        await using var app = await Host(root.Path, Environments.Development);
        var path = Path.Combine(root.Path, "scry.description.json");
        await File.WriteAllTextAsync(path, "{\"version\":1}");

        app.WriteScryDescription(path);

        await Assert.That(await File.ReadAllTextAsync(path)).StartsWith("{\"version\":2,");
    }

    [Test]
    public async Task OutsideDevelopmentNothingIsWrittenUnlessAsked()
    {
        using var root = new TemporaryDirectory();
        await using var app = await Host(root.Path, Environments.Production);

        app.WriteScryDescription("unasked.json");
        app.WriteScryDescription("asked.json", always: true);

        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(Path.Combine(root.Path, "unasked.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(root.Path, "asked.json"))).IsTrue();
        }
    }

    static async Task<WebApplication> Host(string contentRoot, string environment)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                ContentRootPath = contentRoot,
                EnvironmentName = environment
            });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => TestContext.CreateSeeded());
        builder.Services.AddScry<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            // The test model opts in a source with no table, to pin how it is named.
            options.AllowUnmappedSources = true;
        });

        var app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();
        return app;
    }

    sealed class TemporaryDirectory :
        IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("scry-description-").FullName;

        public void Dispose() =>
            Directory.Delete(Path, recursive: true);
    }
}
