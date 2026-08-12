/// <summary>
/// The temporal surface beyond a date's own parts: an elapsed time's components, the sub-millisecond
/// parts, and the conversions between the four temporal types. Each is executed against LocalDB, so
/// the provider's translation is covered rather than only the wire's vocabulary — several neighbours
/// of these functions are deliberately absent precisely because EF refuses them.
/// </summary>
[TestFixture]
public class TemporalPartTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record ShiftRow(string Name);

    record OrderRow(string Region);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task ElapsedTimeParts()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientTimeSpanParts
        var rows = await client.Source<Shift>("Shift")
            .Where(_ => _.Duration.Hours == 7 &&
                        _.Duration.Minutes == 30 &&
                        _.Duration.Seconds == 15)
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();
        // end-snippet

        Assert.That(rows.Single().Name, Is.EqualTo("Early"));
    }

    // The sub-second parts are each within the unit above, so both rows read zero for them — what is
    // pinned here is that they translate at all, since SQL Server counts them from the whole second
    // and the server has to take the remainder.
    [Test]
    public async Task SubSecondParts()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Shift>("Shift")
            .CountAsync(_ => _.Duration.Milliseconds == 0 &&
                             _.Duration.Microseconds == 0 &&
                             _.Duration.Nanoseconds == 0);

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task DateSubSecondParts()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Order>("Order")
            .CountAsync(_ => _.Placed.Microsecond == 0 && _.Placed.Nanosecond == 0);

        Assert.That(count, Is.EqualTo(3));
    }

    // TimeOfDay reads a date's time half as an elapsed time, so its own parts read off the result.
    [Test]
    public async Task TimeOfDayComposesWithTheElapsedParts()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Placed.TimeOfDay.Hours == 9)
            .Select(_ => new OrderRow(_.Region))
            .ToListAsync();

        Assert.That(rows.Single().Region, Is.EqualTo("North"));
    }

    [Test]
    public async Task DayNumber()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Shift>("Shift")
            .Where(_ => _.Day.DayNumber == new Date(2026, 3, 4).DayNumber)
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Early"));
    }

    [Test]
    public async Task ReadingADateAsItsHalves()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => Date.FromDateTime(_.Placed).Year == 2026 &&
                        Time.FromDateTime(_.Placed).Hour == 9)
            .Select(_ => new OrderRow(_.Region))
            .ToListAsync();

        Assert.That(rows.Single().Region, Is.EqualTo("North"));
    }

    [Test]
    public async Task ReadingAnElapsedTimeAsATime()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Shift>("Shift")
            .Where(_ => Time.FromTimeSpan(_.Duration).Hour == 9)
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Late"));
    }

    // The composition back the other way: a date and a time into one timestamp, both read off the row.
    [Test]
    public async Task ComposingADateAndATime()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Shift>("Shift")
            .Where(_ => _.Day.ToDateTime(_.Start).Month == 7)
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Late"));
    }

    [Test]
    public async Task UnixTime()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var seconds = await client.Source<Shift>("Shift")
            .CountAsync(_ => _.Stamped.ToUnixTimeSeconds() > 0);

        var milliseconds = await client.Source<Shift>("Shift")
            .CountAsync(_ => _.Stamped.ToUnixTimeMilliseconds() > 0);

        Assert.Multiple(() =>
        {
            Assert.That(seconds, Is.EqualTo(2));
            Assert.That(milliseconds, Is.EqualTo(2));
        });
    }

    // A whole total is a division rather than a part, and no provider translates one — so it is not in
    // the set. Like any member with no function behind it, it reads as an ordinary path segment and is
    // refused by the server; the analyzer reports it at the call site first.
    [Test]
    public void TotalsAreNotCarried()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<ScryValidationException>(
            () => client.Source<Shift>("Shift")
                .Where(_ => _.Duration.TotalHours > 1)
                .Select(_ => new ShiftRow(_.Name))
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("Duration"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
