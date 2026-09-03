using System.Linq.Expressions;

/// <summary>
/// How a temporal constant is spelled on the wire. Each of these values reaches the server as text and
/// is parsed back into the member's own type, so a spelling that drops part of the value drops it
/// silently — the filter still runs, against something the client never wrote.
/// </summary>
[TestFixture]
public class TemporalConstantTests
{
    // ReSharper disable once NotAccessedPositionalProperty.Local
    record ShiftRow(string Name);

    // A time of day's default text is "05:06" — the seconds are not in it, so a filter on a whole
    // minute was the most this could ever match.
    [Test]
    public void ATimeOfDayCarriesItsSeconds() =>
        Assert.That(
            ShiftConstant(_ => _.Start == new Time(5, 6, 7, 123)).Value,
            Is.EqualTo("05:06:07.1230000"));

    // An offset's default text stops at whole seconds.
    [Test]
    public void AnOffsetCarriesItsSubSecondPart() =>
        Assert.That(
            ShiftConstant(_ => _.Stamped == new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.FromHours(2))).Value,
            Is.EqualTo("2026-03-04T05:06:07.1230000+02:00"));

    // A local timestamp travels as the wall clock it names and nothing more. Carrying the client's
    // offset would leave the server to read it against its own zone, and the same request would then
    // bind a different value on a deployment in another one.
    [Test]
    public void ALocalTimestampCarriesNoOffset() =>
        Assert.That(
            OrderConstant(_ => _.Placed > new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Local)).Value,
            Is.EqualTo("2026-09-03T00:00:00.0000000"));

    // UTC says so, and is read back as the same wall clock everywhere.
    [Test]
    public void AUtcTimestampCarriesItsDesignator() =>
        Assert.That(
            OrderConstant(_ => _.Placed > new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc)).Value,
            Is.EqualTo("2026-09-03T00:00:00.0000000Z"));

    [Test]
    public void AnUnspecifiedTimestampCarriesNeither() =>
        Assert.That(
            OrderConstant(_ => _.Placed > new DateTime(2026, 9, 3)).Value,
            Is.EqualTo("2026-09-03T00:00:00.0000000"));

    // The payoff, against a real database: the seeded row starts at 06:15:30, which a constant
    // truncated to the minute could not match.
    [Test]
    public async Task ATimeOfDayFilterMatchesTheSeededRow()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = await ClientFor(context).Source<Shift>("Shift")
            .Where(_ => _.Start == new Time(6, 15, 30))
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Early"));
    }

    // The same in the other direction, which is the worse failure: the seeded row is stamped at a
    // whole second, so a constant truncated to one matched a filter that asked for a moment after it.
    [Test]
    public async Task AnOffsetFilterDistinguishesASubSecondMoment()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var onTheSecond = await client.Source<Shift>("Shift")
            .Where(_ => _.Stamped == new DateTimeOffset(2026, 3, 4, 6, 15, 30, TimeSpan.FromHours(2)))
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        var justAfter = await client.Source<Shift>("Shift")
            .Where(_ => _.Stamped == new DateTimeOffset(2026, 3, 4, 6, 15, 30, 123, TimeSpan.FromHours(2)))
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(onTheSecond.Single().Name, Is.EqualTo("Early"));
            Assert.That(justAfter, Is.Empty);
        });
    }

    static ConstNode ShiftConstant(Expression<Func<Shift, bool>> predicate) =>
        ConstantIn(Client().Source<Shift>("Shift", ["Name"]).Where(predicate).ToScryRequest());

    static ConstNode OrderConstant(Expression<Func<Order, bool>> predicate) =>
        ConstantIn(Client().Source<Order>("Order", ["Region"]).Where(predicate).ToScryRequest());

    static ConstNode ConstantIn(QueryRequest request) =>
        (ConstNode) ((BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate).Right;

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
