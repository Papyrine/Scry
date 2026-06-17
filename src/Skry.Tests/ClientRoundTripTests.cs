using Skry;
using Skry.Client;

namespace Skry.Tests;

[TestFixture]
public class ClientRoundTripTests
{
    record EmployeeRow(string Name, Status Status, string? ManagerName);

    record OrderSummary(string Region, decimal Total, int Count);

    [Test]
    public async Task WhereOrderBySelect()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var prefix = "A";

        var rows = await client.Source<Employee>("Employee")
            .Where(e => e.Status == Status.FullTime && e.Name.StartsWith(prefix))
            .OrderBy(e => e.Name)
            .Select(e => new EmployeeRow(e.Name, e.Status, e.Manager!.Name))
            .ToSkryListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task GroupByAggregate()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(o => o.Region)
            .Select(g => new OrderSummary(g.Key, g.Sum(x => x.Amount), g.Count()))
            .ToSkryListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task ClosureCapturedConstant()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var wanted = Status.Contractor;

        var rows = await client.Source<Employee>("Employee")
            .Where(e => e.Status == wanted)
            .Select(e => new EmployeeRow(e.Name, e.Status, e.Manager!.Name))
            .ToSkryListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task CountTerminal()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .Where(e => e.Active)
            .CountSkryAsync();

        await Assert.ThatAsync(() => Task.FromResult(count), Is.EqualTo(3));
    }

    [Test]
    public async Task AnyTerminal()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var prefix = "Z";

        var any = await client.Source<Employee>("Employee")
            .Where(e => e.Name.StartsWith(prefix))
            .AnySkryAsync();

        Assert.That(any, Is.False);
    }

    [Test]
    public void UnsupportedProjectionThrows()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Employee>("Employee").Select(e => e.Name).ToSkryListAsync());
    }

    static SkryClient ClientFor(TestContext context)
    {
        var processor = SkryProcessor.Create(options =>
        {
            options.UseModel<TestContext>();
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        });

        return new((request, _) => Task.FromResult(processor.Execute(request, context)));
    }
}
