/// <summary>
/// A nested projection into an optional struct complex member. The member travels as a
/// <c>Nullable&lt;T&gt;</c>, which the validator once handed to the nested projection unwrapped only
/// when the path went on through it — so projecting <em>into</em> the member was refused as a type
/// that is not queryable, while reading a leaf out of it was fine. The client's half: a leaf read
/// through the Nullable's Value keeps no "Value" segment, in a path any more than at its end.
/// </summary>
[TestFixture]
public class OptionalStructProjectionTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeDesk(string Name, Desk? Desk);

    record Desk(string? Room, string? Extension);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ProjectsIntoTheStruct()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeDesk(_.Name, new Desk(_.Workstation!.Value.Room, _.Workstation!.Value.Extension)))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public void TheWireShapeIsANestedProjection()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var request = client.Source<Employee>("Employee")
            .Select(_ => new EmployeeDesk(_.Name, new Desk(_.Workstation!.Value.Room, _.Workstation!.Value.Extension)))
            .ToScryRequest();

        var nested = request.Pipeline.OfType<SelectOp>().Single().Projection.Members
            .Select(_ => _.Value)
            .OfType<NestedValue>()
            .Single();
        Assert.That(nested.Path, Is.EqualTo(new[] {"Workstation"}));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
