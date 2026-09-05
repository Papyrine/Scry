using System.Linq.Expressions;

/// <summary>
/// A call inside a query lambda that reads nothing from the row is closure state, and is evaluated
/// into the constant it stands for before the request is sent.
/// </summary>
/// <remarks>
/// The analyzer already reads the rule this way — <c>ExpressionRules.Call</c> reports nothing for a
/// call touching neither the row nor anything reached from it — so a translator that refused one
/// instead left compiled client code green and broke it at runtime, with nothing between the two to
/// say so. That is the divergence these pin. Each shape here dispatches on a declaring type the
/// translator has a function table for, and each used to refuse a name absent from that table without
/// first asking whether the call read the row at all. The refusals are kept alongside: the same call
/// spelled over the row still has nowhere to run.
/// </remarks>
[TestFixture]
public class ClosureFoldTests
{
    // The shape that prompted this. A relative date is the client's own clock, so it belongs in the
    // request as a value; the temporal dispatch had a wire function for the name, but only for the
    // spelling that reads the row, and refused this one rather than folding it. The part read off
    // the folded value folds with it, so the whole operand is the constant.
    [Test]
    public void ATemporalConversionOverClosureState()
    {
        var predicate = PredicateOf(_ => _.Placed.Day == Date.FromDateTime(DateTime.UtcNow).Day);

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    // A temporal name the wire carries no function for at all — the fold is what makes it a value
    // rather than a refusal.
    [Test]
    public void ATemporalMethodOffTheSurfaceOverClosureState()
    {
        var predicate = PredicateOf(_ => _.Placed > DateTime.UtcNow.AddTicks(1));

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    [Test]
    public void AMathMethodOffTheSurfaceOverClosureState()
    {
        var predicate = PredicateOf(_ => _.Amount > (decimal) Math.Clamp(1.5, 0, 2));

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    [Test]
    public void AStringMethodOffTheSurfaceOverClosureState()
    {
        var predicate = PredicateOf(_ => _.Region == "x".PadLeft(3));

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    // A property read off closure state. The temporal dispatch carried it as a function over the
    // constant, and for an offset — which travels as text — the server refused to read a part of a
    // string; a date's own parts happened to survive because a date carries a typed tag.
    [Test]
    public void ATemporalPropertyOverClosureState()
    {
        var cutoff = new DateTimeOffset(2026, 3, 4, 6, 15, 30, TimeSpan.FromHours(2));
        var predicate = PredicateOf(_ => _.Placed.Year == cutoff.Year);

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    [Test]
    public void AnElapsedTimesPartOverClosureState()
    {
        var span = TimeSpan.FromHours(5);
        var predicate = PredicateOf(_ => _.Amount > span.Hours);

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    [Test]
    public void AStringLengthOverClosureState()
    {
        var text = "north";
        var predicate = PredicateOf(_ => _.Region.Length == text.Length);

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    // The shape from a page: the year of the client's clock, read off an offset. Round-tripped, since
    // the refusal was the server's.
    [Test]
    public async Task AClockPartRoundTrips()
    {
        await using var context = TestContext.CreateSeeded();
        var client = new ScryClient((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
        var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        var count = await client.Source<Order>("Order")
            .Where(_ => _.Placed.Year == now.Year)
            .CountAsync();

        Assert.That(count, Is.EqualTo(context.Orders.Count(_ => _.Placed.Year == 2026)));
    }

    // Formatting is refused because the SQL that would express it reads the server's language. A value
    // formatted here has already answered that objection.
    [Test]
    public void AFormattedToStringOverClosureState()
    {
        var stamped = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var predicate = PredicateOf(_ => _.Region == stamped.ToString("yyyy", CultureInfo.InvariantCulture));

        Assert.That(predicate.Right, Is.InstanceOf<ConstNode>());
    }

    // The other half of the rule: a call that does read the row cannot be evaluated here, and has no
    // wire function to become either.
    [Test]
    public void ATemporalMethodOffTheSurfaceOverTheRowIsStillRefused() =>
        Assert.Throws<NotSupportedException>(
            () => PredicateOf(_ => _.Placed.AddTicks(1) > DateTime.UtcNow));

    [Test]
    public void AStringMethodOffTheSurfaceOverTheRowIsStillRefused() =>
        Assert.Throws<NotSupportedException>(
            () => PredicateOf(_ => _.Region.PadLeft(3) == "x"));

    [Test]
    public void AFormattedToStringOverTheRowIsStillRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => PredicateOf(_ => _.Placed.ToString("yyyy", CultureInfo.InvariantCulture) == "2026"));

        Assert.That(exception!.Message, Does.StartWith("ToString with a format is not supported"));
    }

    static BinaryNode PredicateOf(Expression<Func<Order, bool>> predicate)
    {
        var request = Client().Source<Order>("Order", ["Region"])
            .Where(predicate)
            .ToScryRequest();

        return (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));
}
