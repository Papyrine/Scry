namespace Scry.Tests;

[TestFixture]
public class ClientRoundTripTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeRow(string Name, Status Status, string? ManagerName);

    record OrderSummary(string Region, decimal Total, int Count);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public Task ToScryRequestTranslatesWithoutExecuting()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Closure-captured values (wanted, prefix, take) force the translator's
        // Expression.Compile().DynamicInvoke() evaluation path — the same path the browser explorer
        // exercises in WebAssembly.
        var wanted = Status.FullTime;
        var prefix = "A";
        var take = 5;

        var request = client.Source<Employee>("Employee")
            .Where(_ => _.Active &&
                        _.Status == wanted &&
                        _.Name.StartsWith(prefix))
            .OrderBy(_ => _.Name)
            .Take(take)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToScryRequest();

        return Verify(request);
    }

    [Test]
    public async Task WhereOrderBySelect()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var prefix = "A";

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Status == Status.FullTime &&
                        _.Name.StartsWith(prefix))
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToScryListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task GroupByAggregate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new OrderSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
            .ToScryListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task ClosureCapturedConstant()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var wanted = Status.Contractor;

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Status == wanted)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToScryListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task CountTerminal()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .Where(_ => _.Active)
            .CountScryAsync();

        await Assert.ThatAsync(() => Task.FromResult(count), Is.EqualTo(3));
    }

    [Test]
    public async Task AnyTerminal()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var prefix = "Z";

        var any = await client.Source<Employee>("Employee")
            .Where(_ => _.Name.StartsWith(prefix))
            .AnyScryAsync();

        Assert.That(any, Is.False);
    }

    [Test]
    public void UnsupportedProjectionThrows()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Employee>("Employee")
                .Select(_ => _.Name)
                .ToScryListAsync());
    }

    static ScryClient ClientFor(TestContext context)
    {
        var processor = ScryProcessor.Create(options =>
        {
            options.UseModel<TestContext>();
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        });

        return new((request, _) => Task.FromResult(processor.Execute(request, context)));
    }
}
