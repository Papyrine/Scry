/// <summary>
/// A client asks for a case sensitivity; the server decides which collation implements it. The
/// collation is the one value that cannot be a query parameter — it is emitted into the SQL text —
/// so it is never carried on the wire.
/// </summary>
[TestFixture]
public class CollationTests
{
    // ReSharper disable once NotAccessedPositionalProperty.Local
    record NameRow(string Name);

    static ScryProcessor Collating() =>
        ScryProcessor.Create<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.CaseSensitiveCollation = "Latin1_General_CS_AS";
                options.CaseInsensitiveCollation = "Latin1_General_CI_AS";
            });

    [Test]
    public async Task CaseSensitiveComparisonNarrowsAnInsensitiveDatabase()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Collating());

        // The database's own collation is case-insensitive, so the default match finds Alice from a
        // lowercase term. Asking for case sensitivity does not.
        var insensitive = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.Name.StartsWith("ali"));
        var sensitive = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.Name.StartsWith("ali", StringComparison.Ordinal));
        var matching = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.Name.StartsWith("Ali", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(insensitive, Is.EqualTo(1));
            Assert.That(sensitive, Is.Zero);
            Assert.That(matching, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CaseInsensitiveComparisonIsExplicit()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Collating());

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name.Contains("LIC", StringComparison.OrdinalIgnoreCase))
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Alice"));
    }

    [Test]
    public async Task EqualsUnderACollation()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Collating());

        var sensitive = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.Name.Equals("alice", StringComparison.Ordinal));

        Assert.That(sensitive, Is.Zero);
    }

    [Test]
    public void AnUnconfiguredCollationIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, SharedProcessor.Instance);

        // The shared processor configures none, so the feature is off rather than guessed at.
        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Employee>("Employee")
                .CountAsync(_ => _.Name.StartsWith("al", StringComparison.Ordinal)));

        Assert.That(exception!.Message, Does.Contain("collation configured"));
    }

    [Test]
    public void TheCollationIsNeverCarriedOnTheWire()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Collating());

        var request = client.Source<Employee>("Employee")
            .Where(_ => _.Name.StartsWith("al", StringComparison.Ordinal))
            .ToScryRequest();

        // A request names only the sensitivity it wants. The collation is a server setting, so no
        // request can put a string of its own choosing into the SQL text.
        var json = ScryJson.Serialize(request);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("CaseSensitive"));
            Assert.That(json, Does.Not.Contain("Latin1"));
        });
    }

    [Test]
    public void AMalformedCollationIsRefusedAtStartup()
    {
        // The wire cannot carry a collation, so this guards the remaining path: a deployment wiring
        // the option up from somewhere it does not control. Checked once, at startup.
        var exception = Assert.Throws<Exception>(
            () => ScryProcessor.Create<TestContext>(
                options =>
                {
                    options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                    options.CaseSensitiveCollation = "Latin1_General_CS_AS; DROP TABLE Orders --";
                }));

        Assert.That(exception!.Message, Does.Contain("plain collation name"));
    }

    [Test]
    public void AWellFormedCollationIsAccepted() =>
        Assert.DoesNotThrow(() => Collating());

    static ScryClient ClientFor(TestContext context, ScryProcessor processor) =>
        new((request, _) => Task.FromResult(processor.Execute(request, context)));
}
