/// <summary>
/// A generated entry point hands its scalar member names to <c>Source</c>, so a query that writes no
/// <c>Select</c> still projects them explicitly. That keeps the response keyed by the names the client
/// was generated with, instead of whatever the server's current model calls them.
/// </summary>
public class DefaultProjectionTests
{
    // ReSharper disable once NotAccessedPositionalProperty.Local
    record EmployeeRow(string Name);

    [Test]
    public Task NoSelectProjectsTheClientsMemberNames()
    {
        var request = Client()
            .Source<Employee>("Employee", ["Name", "Status"])
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .ToScryRequest();

        return Verify(request);
    }

    // The payoff: after a member rename the client keeps asking for — and receiving — the name it was
    // generated with, with no need for the server to guess which vintage of client it is talking to.
    // 'FullName' is Employee.Name's previous name, so this stands in for a pre-rename client.
    [Test]
    public async Task ResponseIsKeyedByTheClientsMemberNames()
    {
        using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;

        var request = Client()
            .Source<Employee>("Employee", ["FullName"])
            .OrderBy(_ => _.Name)
            .ToScryRequest();

        var json = ScryJson.Serialize(processor.Execute(request, context));

        await Assert.That(json).Contains("\"fullName\":\"Alice\"");
        await Assert.That(json).DoesNotContain("\"name\"");
    }

    // Count and Any return a scalar; bolting a member projection onto them would be pointless SQL.
    [Test]
    public async Task ScalarTerminalsAreNotProjected()
    {
        var source = Client().Source<Employee>("Employee", ["Name"]);

        await Assert.That(source.ToScryRequest(new CountOp()).Pipeline.OfType<SelectOp>()).IsEmpty();
        await Assert.That(source.ToScryRequest(new AnyOp(Predicate: null)).Pipeline.OfType<SelectOp>()).IsEmpty();
    }

    // The validator rejects a terminal predicate once a Select is present, so injecting one would turn
    // a valid hand-built request into an invalid one. It falls back to the server's default instead.
    [Test]
    public async Task TerminalCarryingItsOwnPredicateIsNotProjected()
    {
        var source = Client().Source<Employee>("Employee", ["Name"]);
        var predicate = new BinaryNode(
            BinaryOp.Equal,
            new MemberNode(["Name"]),
            new ConstNode("Alice", ClrTypeTag.String));

        var first = source.ToScryRequest(new FirstOp(OrDefault: false, predicate));
        var single = source.ToScryRequest(new SingleOp(OrDefault: false, predicate));

        await Assert.That(first.Pipeline.OfType<SelectOp>()).IsEmpty();
        await Assert.That(single.Pipeline.OfType<SelectOp>()).IsEmpty();
    }

    // A terminal with no predicate of its own is the normal case and does get projected.
    [Test]
    public async Task PredicatelessRowTerminalIsProjected()
    {
        var request = Client()
            .Source<Employee>("Employee", ["Name"])
            .ToScryRequest(new FirstOp(OrDefault: false, Predicate: null));

        await Assert.That(request.Pipeline.OfType<SelectOp>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitSelectIsNotDuplicated()
    {
        var request = Client()
            .Source<Employee>("Employee", ["Name", "Status"])
            .Select(_ => new EmployeeRow(_.Name))
            .ToScryRequest();

        await Assert.That(request.Pipeline.OfType<SelectOp>().Count()).IsEqualTo(1);
    }

    // A source built by hand carries no member list and no fixed model to disappoint, so it still
    // falls back to the server's default projection.
    [Test]
    public async Task HandBuiltSourceFallsBackToTheServerDefault()
    {
        var request = Client().Source<Employee>("Employee").ToScryRequest();

        await Assert.That(request.Pipeline.OfType<SelectOp>()).IsEmpty();
    }

    [Test]
    public async Task ProjectionPrecedesTheTerminal()
    {
        var request = Client()
            .Source<Employee>("Employee", ["Name"])
            .OrderBy(_ => _.Name)
            .ToScryRequest(new PageOp(Size: 2));

        // The validator rejects any operator after a terminal, so order matters here.
        await Assert.That(request.Pipeline[^1]).IsAssignableTo<PageOp>();
        await Assert.That(request.Pipeline[^2]).IsAssignableTo<SelectOp>();
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));

}
