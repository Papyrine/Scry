using System.Linq.Expressions;

/// <summary>
/// Math.Round's rounding mode. SQL's ROUND rounds away from zero, so that mode is honoured by being
/// dropped, and any other is refused. The mode once travelled as an operand: with digits it was
/// silently discarded, and alone it was sent as the digits and refused by the server as a number.
/// </summary>
[TestFixture]
public class RoundingModeTests
{
    [Test]
    public void AwayFromZeroWithDigitsIsDropped()
    {
        var call = RoundOf(_ => Math.Round(_.Amount, 2, MidpointRounding.AwayFromZero) > 1);

        Assert.Multiple(() =>
        {
            Assert.That(call.Function, Is.EqualTo(KnownFunction.MathRound));
            Assert.That(call.Arguments, Has.Count.EqualTo(1));
            Assert.That(((ConstNode) call.Arguments[0]).Value, Is.EqualTo("2"));
        });
    }

    [Test]
    public void AwayFromZeroAloneIsDropped()
    {
        var call = RoundOf(_ => Math.Round(_.Amount, MidpointRounding.AwayFromZero) > 1);

        Assert.That(call.Arguments, Is.Empty);
    }

    [Test]
    public void AnotherModeIsRefused()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => RoundOf(_ => Math.Round(_.Amount, 2, MidpointRounding.ToEven) > 1));

        Assert.That(exception!.Message, Does.Contain("AwayFromZero"));
    }

    static CallNode RoundOf(Expression<Func<Order, bool>> predicate)
    {
        var request = Client().Source<Order>("Order", ["Region"])
            .Where(predicate)
            .ToScryRequest();

        return (CallNode) ((BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate).Left;
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));
}
