/// <summary>
/// A projection written as a constructor call with an initializer — new Row(_.Id) { Name = _.Name }
/// — names members in both halves, and both have to reach the wire: the arguments were once dropped,
/// so the row came back with its key at default and nothing said so.
/// </summary>
public class InitializerProjectionTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeRow(int Id)
    {
        public string Name { get; init; } = "";

        public string? Department { get; init; }
    }

    record EmployeeCard(string Name)
    {
        public DepartmentCard? Department { get; init; }
    }

    record DepartmentCard(string Name)
    {
        public int Id { get; init; }
    }
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ConstructorArgumentsAndAssignmentsAreBothProjected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Id)
            {
                Name = _.Name,
                Department = _.Department!.Name
            })
            .ToListAsync();

        string[] names = ["Aaron", "Alice", "Bob", "Carol"];
        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(names, CollectionOrdering.Matching);
            // The argument half: a key is never zero, and each row has its own.
            await Assert.That(rows.Select(_ => _.Id)).All(_ => _ > 0);
            await Assert.That(rows.Select(_ => _.Id)).HasDistinctItems();
            await Assert.That(rows.Select(_ => _.Department)).All(_ => _ is not null);
        }
    }

    [Test]
    public async Task ANestedObjectProjectsBothHalvesToo()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var cards = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeCard(_.Name)
            {
                Department = new(_.Department!.Name)
                {
                    Id = _.Department.Id
                }
            })
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(cards).Count().IsEqualTo(4);
            await Assert.That(cards.Select(_ => _.Department!.Name)).All(_ => _.Length != 0);
            await Assert.That(cards.Select(_ => _.Department!.Id)).All(_ => _ > 0);
        }
    }

    [Test]
    public async Task AMemberSetInBothHalvesIsRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Legal C# — the positional member is init-only — but two values for one member is a
        // projection no wire member can carry, and a silent choice between them would be worse.
        var exception = Assert.ThrowsExactly<NotSupportedException>(() =>
            client.Source<Employee>("Employee")
                .Select(_ => new EmployeeRow(_.Id)
                {
                    Id = _.DepartmentId
                })
                .ToScryRequest());

        await Assert.That(exception!.Message).Contains("projected twice");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
