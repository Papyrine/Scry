namespace Scry.Tests;

/// <summary>
/// A generated entry point hands its scalar member names to <c>Source</c>, so a query that writes no
/// <c>Select</c> still projects them explicitly. That keeps the response keyed by the names the client
/// was generated with, instead of whatever the server's current model calls them.
/// </summary>
[TestFixture]
public class DefaultProjectionTests
{
    // ReSharper disable once NotAccessedPositionalProperty.Local
    record EmployeeRow(string Name);

    [Test]
    public Task NoSelectProjectsTheClientsMemberNames()
    {
        var request = Client().Source<Employee>("Employee", ["Name", "Status"])
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .ToScryRequest();

        return Verify(request);
    }

    // The payoff: after a member rename the client keeps asking for — and receiving — the name it was
    // generated with, with no need for the server to guess which vintage of client it is talking to.
    // 'FullName' is Employee.Name's previous name, so this stands in for a pre-rename client.
    [Test]
    public void ResponseIsKeyedByTheClientsMemberNames()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Processor();

        var request = Client().Source<Employee>("Employee", ["FullName"])
            .OrderBy(_ => _.Name)
            .ToScryRequest();

        var json = ScryJson.Serialize(processor.Execute(request, context));

        Assert.That(json, Does.Contain("\"fullName\":\"Alice\""));
        Assert.That(json, Does.Not.Contain("\"name\""));
    }

    // Count and Any return a scalar; bolting a member projection onto them would be pointless SQL.
    [Test]
    public void ScalarTerminalsAreNotProjected()
    {
        var source = Client().Source<Employee>("Employee", ["Name"]);

        Assert.That(source.ToScryRequest(new CountOp()).Pipeline.OfType<SelectOp>(), Is.Empty);
        Assert.That(source.ToScryRequest(new AnyOp(Predicate: null)).Pipeline.OfType<SelectOp>(), Is.Empty);
    }

    // The validator rejects a terminal predicate once a Select is present, so injecting one would turn
    // a valid hand-built request into an invalid one. It falls back to the server's default instead.
    [Test]
    public void TerminalCarryingItsOwnPredicateIsNotProjected()
    {
        var source = Client().Source<Employee>("Employee", ["Name"]);
        var predicate = new BinaryNode(
            BinaryOp.Equal,
            new MemberNode(["Name"]),
            new ConstNode("Alice", ClrTypeTag.String));

        var first = source.ToScryRequest(new FirstOp(OrDefault: false, predicate));
        var single = source.ToScryRequest(new SingleOp(OrDefault: false, predicate));

        Assert.That(first.Pipeline.OfType<SelectOp>(), Is.Empty);
        Assert.That(single.Pipeline.OfType<SelectOp>(), Is.Empty);
    }

    // A terminal with no predicate of its own is the normal case and does get projected.
    [Test]
    public void PredicatelessRowTerminalIsProjected()
    {
        var request = Client().Source<Employee>("Employee", ["Name"])
            .ToScryRequest(new FirstOp(OrDefault: false, Predicate: null));

        Assert.That(request.Pipeline.OfType<SelectOp>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void ExplicitSelectIsNotDuplicated()
    {
        var request = Client().Source<Employee>("Employee", ["Name", "Status"])
            .Select(_ => new EmployeeRow(_.Name))
            .ToScryRequest();

        Assert.That(request.Pipeline.OfType<SelectOp>().Count(), Is.EqualTo(1));
    }

    // A source built by hand carries no member list and no fixed model to disappoint, so it still
    // falls back to the server's default projection.
    [Test]
    public void HandBuiltSourceFallsBackToTheServerDefault()
    {
        var request = Client().Source<Employee>("Employee").ToScryRequest();

        Assert.That(request.Pipeline.OfType<SelectOp>(), Is.Empty);
    }

    [Test]
    public void ProjectionPrecedesTheTerminal()
    {
        var request = Client().Source<Employee>("Employee", ["Name"])
            .OrderBy(_ => _.Name)
            .ToScryRequest(new PageOp(Size: 2));

        // The validator rejects any operator after a terminal, so order matters here.
        Assert.That(request.Pipeline[^1], Is.InstanceOf<PageOp>());
        Assert.That(request.Pipeline[^2], Is.InstanceOf<SelectOp>());
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
}
