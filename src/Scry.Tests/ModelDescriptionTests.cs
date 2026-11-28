/// <summary>
/// The description written at build time from a model's DLL, read as the source generator reads it,
/// against the one the server gives of the same model: byte for byte, since a client generated from
/// either has to be the one the server validates. The test model carries every shape the two readers
/// have disagreed on.
/// </summary>
public class ModelDescriptionTests
{
    static string Model => typeof(TestContext).Assembly.Location;

    [Test]
    public async Task TheBuildTimeDescriptionIsTheServers()
    {
        var served = SharedProcessor.Instance.Describe();
        var extract = MetadataModelReader.Read(Model);

        var built = ModelDescriber.Describe(extract, served.MaxPageSize, served.QueryUrlLimit);

        using (Assert.Multiple())
        {
            await Assert.That(ModelDescriber.Problems(extract)).IsEmpty();
            await Assert.That(ScryJson.Serialize(built)).IsEqualTo(ScryJson.Serialize(served));
        }
    }

    // Scry.Server is not referenced by the command, so its defaults are spelled there.
    [Test]
    public async Task TheCommandsDefaultsAreTheServers()
    {
        var options = new ScryOptions(typeof(TestContext));

        using (Assert.Multiple())
        {
            await Assert.That(options.MaxPageSize).IsEqualTo(DescribeCommand.DefaultMaxPageSize);
            await Assert.That(options.QueryUrlLimit).IsEqualTo(DescribeCommand.DefaultQueryUrlLimit);
        }
    }

    [Test]
    public async Task TheCommandWritesTheDescriptionAndLeavesAnUnchangedOneAlone()
    {
        var directory = Directory.CreateTempSubdirectory("scry-describe-").FullName;
        try
        {
            var path = Path.Combine(directory, "client", "scry.description.json");
            var output = new StringWriter();

            var first = DescribeCommand.Run(["--model", Model, "--output", path, "--max-page-size", "50"], output, TextWriter.Null);
            var before = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, before);
            var second = DescribeCommand.Run(["--model", Model, "--output", path, "--max-page-size", "50"], output, TextWriter.Null);

            var written = ScryJson.DeserializeIntrospection(await File.ReadAllTextAsync(path));
            using (Assert.Multiple())
            {
                await Assert.That(first).IsEqualTo(0);
                await Assert.That(second).IsEqualTo(0);
                await Assert.That(written.MaxPageSize).IsEqualTo(50);
                await Assert.That(written.SchemaStamp).IsEqualTo(SharedProcessor.Instance.SchemaStamp);
                await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(before);
                await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!)).Count().IsEqualTo(1);
                await Assert.That(output.ToString()).Contains("is already the description of");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AModelThatCannotBeReadIsRefusedAndNothingIsWritten()
    {
        var directory = Directory.CreateTempSubdirectory("scry-describe-").FullName;
        try
        {
            var path = Path.Combine(directory, "scry.description.json");
            var error = new StringWriter();

            var code = DescribeCommand.Run(["--model", Path.Combine(directory, "Missing.dll"), "--output", path], TextWriter.Null, error);

            using (Assert.Multiple())
            {
                await Assert.That(code).IsEqualTo(1);
                await Assert.That(error.ToString()).Contains("does not exist");
                await Assert.That(File.Exists(path)).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
