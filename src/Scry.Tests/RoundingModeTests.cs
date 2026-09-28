/// <summary>
/// Math.Round's rounding mode. SQL's ROUND rounds away from zero, so that mode is honoured by being
/// dropped, and any other is refused. The mode once travelled as an operand: with digits it was
/// silently discarded, and alone it was sent as the digits and refused by the server as a number.
/// </summary>
public class RoundingModeTests
{
    [Test]
    public async Task AwayFromZeroWithDigitsIsDropped()
    {
        var call = RoundOf(_ => Math.Round(_.Amount, 2, MidpointRounding.AwayFromZero) > 1);

        using (Assert.Multiple())
        {
            await Assert.That(call.Function).IsEqualTo(KnownFunction.MathRound);
            await Assert.That(call.Arguments).Count().IsEqualTo(1);
            await Assert.That(((ConstNode) call.Arguments[0]).Value).IsEqualTo("2");
        }
    }

    [Test]
    public async Task AwayFromZeroAloneIsDropped()
    {
        var call = RoundOf(_ => Math.Round(_.Amount, MidpointRounding.AwayFromZero) > 1);

        await Assert.That(call.Arguments).IsEmpty();
    }

    [Test]
    public async Task AnotherModeIsRefused()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => RoundOf(_ => Math.Round(_.Amount, 2, MidpointRounding.ToEven) > 1));

        await Assert.That(exception!.Message).Contains("AwayFromZero");
    }

    static CallNode RoundOf(Expression<Func<Order, bool>> predicate)
    {
        var request = Client()
            .Source<Order>("Order", ["Region"])
            .Where(predicate)
            .ToScryRequest();

        return (CallNode) ((BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate).Left;
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));
}
