/// <summary>
/// The two spellings EF translates that carry no wire vocabulary of their own: <c>Equals</c>, which is
/// the <c>==</c> comparison written as a method, and a nullable's <c>Value</c> / <c>HasValue</c>, which
/// are the member itself and a comparison against null. Both are client-side rewrites, so each is
/// executed against LocalDB here to pin that what they rewrite into survives validation and rebinding.
/// </summary>
[TestFixture]
public class EqualityAndOptionalTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record NameRow(string Name);

    record OrderShape(string Region, decimal Amount);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task EqualsOnAStringIsTheComparison()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientEquals
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name.Equals("Alice"))
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Single().Name, Is.EqualTo("Alice"));
    }

    [Test]
    public async Task TheStaticSpellingMeansTheSame()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => string.Equals(_.Name, "Alice"))
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Alice"));
    }

    [Test]
    public async Task EqualsOnANumber()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Quantity.Equals(3u))
            .Select(_ => new OrderShape(_.Region, _.Amount))
            .ToListAsync();

        Assert.That(rows.Single().Amount, Is.EqualTo(100m));
    }

    // The enum spelling compiles to Object.Equals against a boxed operand, so it arrives with a Convert
    // around its argument rather than as the enum's own overload.
    [Test]
    public async Task EqualsOnAnEnum()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Status.Equals(Status.Contractor))
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Carol"));
    }

    // An Equals reading nothing from the row is closure state, evaluated before the query the way any
    // other constant expression is.
    [Test]
    public async Task EqualsOverClosureStateIsAConstant()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var wanted = "North";

        var count = await client.Source<Order>("Order")
            .CountAsync(_ => wanted.Equals("North") && _.Region == wanted);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task HasValueAsksWhetherTheMemberIsThere()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientHasValue
        var managed = await client.Source<Employee>("Employee")
            .Where(_ => _.ManagerId.HasValue)
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();
        // end-snippet

        Assert.That(managed.Select(_ => _.Name), Is.EqualTo(new[] { "Aaron", "Bob" }));
    }

    [Test]
    public async Task NegatedHasValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var unmanaged = await client.Source<Employee>("Employee")
            .Where(_ => !_.ManagerId.HasValue)
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(unmanaged.Select(_ => _.Name), Is.EqualTo(new[] { "Alice", "Carol" }));
    }

    [Test]
    public async Task ValueIsTheMemberItWraps()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The 250 order has no discount at all, so it is absent rather than compared as a zero.
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Discount!.Value > 6m)
            .Select(_ => new OrderShape(_.Region, _.Amount))
            .ToListAsync();

        Assert.That(rows.Single().Amount, Is.EqualTo(100m));
    }

    // Value in front of a date part: the wrapper is stripped and the function reads the member under
    // it, which is the same node an ordinary DateTime member produces.
    [Test]
    public async Task ValueUnderAFunction()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.ManagerId!.Value > 0)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
