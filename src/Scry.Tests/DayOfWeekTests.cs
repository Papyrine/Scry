/// <summary>
/// Day of week. EF refuses to translate <c>DateTime.DayOfWeek</c> on SQL Server because the obvious
/// SQL for it reads <c>@@DATEFIRST</c> and so answers differently per connection. Scry carries the
/// intent on the wire and builds the deterministic arithmetic server-side instead.
/// </summary>
[TestFixture]
public class DayOfWeekTests
{
    [Test]
    public async Task NumbersTheDaysAsDotNetDoes()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Sku, Day = _.Placed.DayOfWeek})
            .ToListAsync();

        // The same rows, with .NET computing the day of week itself.
        var expected = context.Orders
            .ToList()
            .ToDictionary(_ => _.Sku, _ => _.Placed.DayOfWeek);

        Assert.That(rows, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var row in rows)
            {
                Assert.That(row.Day, Is.EqualTo(expected[row.Sku]), $"Sku {row.Sku}");
            }
        });
    }

    [Test]
    public async Task FiltersByADayOfWeek()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientDayOfWeek
        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Placed.DayOfWeek == DayOfWeek.Wednesday)
            .Select(_ => new {_.Region, _.Placed})
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Select(_ => _.Placed.DayOfWeek), Is.All.EqualTo(DayOfWeek.Wednesday));
        Assert.That(rows, Has.Count.EqualTo(context.Orders.ToList().Count(_ => _.Placed.DayOfWeek == DayOfWeek.Wednesday)));
    }

    [Test]
    public async Task OrdersByTheDayOfWeek()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Placed.DayOfWeek)
            .Select(_ => new {Day = _.Placed.DayOfWeek})
            .ToListAsync();

        Assert.That(rows.Select(_ => (int)_.Day), Is.Ordered);
    }

    [Test]
    public async Task CountsByADayOfWeek()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Grouping *by* a computed key is a separate restriction the client applies to every computed
        // key, so counting a filtered set is how a per-weekday total is asked for today.
        var count = await client.Source<Order>("Order")
            .CountAsync(_ => _.Placed.DayOfWeek == DayOfWeek.Wednesday);

        Assert.That(count, Is.EqualTo(context.Orders.ToList().Count(_ => _.Placed.DayOfWeek == DayOfWeek.Wednesday)));
    }

    [Test]
    public async Task HandlesDatesBeforeTheEpoch()
    {
        // The day count runs negative before 1900, where a single remainder would answer negative.
        // Needs rows the shared read-only seed does not have, so this one owns its database.
        await using var database = await TestContext.CreateIsolated("DayOfWeekEpoch");
        var context = database.Context;

        var early = new[]
        {
            new DateTime(1875, 6, 14),
            new DateTime(1899, 12, 31),
            new DateTime(1600, 2, 29)
        };

        var sku = 4000ul;
        foreach (var date in early)
        {
            context.Orders.Add(new() {Region = "Old", Amount = 1m, Quantity = 1, Sku = sku++, Placed = date, Grade = 'A'});
        }

        await context.SaveChangesAsync();

        var client = ClientFor(context);
        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Sku, Day = _.Placed.DayOfWeek})
            .ToListAsync();

        var expected = early.ToDictionary(_ => _, _ => _.DayOfWeek);

        Assert.Multiple(() =>
        {
            foreach (var date in early)
            {
                var row = rows.Single(_ => _.Sku == 4000ul + (ulong)Array.IndexOf(early, date));
                Assert.That(row.Day, Is.EqualTo(expected[date]), $"{date:yyyy-MM-dd}");
            }
        });
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
