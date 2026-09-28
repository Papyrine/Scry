/// <summary>
/// A conditional with a null in one branch. The client sends the null bare — a cast to the other
/// branch's type is lifting, which it drops — so the server has to read the null's type off the
/// other branch, whichever side the null is on. It once did so for the false branch only, and read a
/// null in the true branch as text.
/// </summary>
public class ConditionalNullBranchTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record DepartmentRow(string Name, int? Department);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ANullTrueBranchTakesTheFalseBranchsType()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new DepartmentRow(_.Name, _.Active ? null : _.DepartmentId))
            .ToListAsync();

        // Bob is the one inactive employee, so his is the one department read.
        await Assert.That(rows.Select(_ => (_.Name, _.Department))).IsEquivalentTo([("Aaron", (int?)null), ("Alice", null), ("Bob", 2), ("Carol", null)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ANullFalseBranchTakesTheTrueBranchsType()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new DepartmentRow(_.Name, _.Active ? _.DepartmentId : null))
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Department)).IsEquivalentTo(new int?[] {1, 1, null, 2}, CollectionOrdering.Matching);
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
