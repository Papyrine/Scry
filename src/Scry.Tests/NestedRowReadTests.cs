/// <summary>
/// A query lambda reads its own row and closure state. A shape that reads the row of an enclosing
/// lambda, or a parameter that is not a row, was compiled as closure state and failed with the
/// expression compiler's message about an undefined variable rather than with what Scry cannot carry.
/// </summary>
public class NestedRowReadTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record TextRow(string Text);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public Task AnOuterRowReadInsideASubquery() =>
        Refuses(_ => _.Lines.Any(line => line.Quantity > _.Id), "'_'");

    [Test]
    public Task AMemberReadOffAnElementOfASubquery() =>
        Refuses(_ => _.Lines.First().Quantity > 1, "'_'");

    [Test]
    public async Task AnIndexedFilter()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => Client().Source<Order>("Order").Where((order, index) => index < 5).ToScryRequest());

        await Assert.That(exception.Message).Contains("'index'");
    }

    // The group read as a value rather than folded: not an aggregate, and once an index past the
    // end of an argument list.
    [Test]
    public void AGroupReadAsText() =>
        Assert.ThrowsExactly<NotSupportedException>(
            () => Client()
                .Source<Order>("Order")
                .GroupBy(_ => _.Region)
                .Select(_ => new TextRow(_.ToString()!))
                .ToScryRequest());

    static async Task Refuses(Expression<Func<Order, bool>> predicate, string mentions)
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => Client().Source<Order>("Order").Where(predicate).ToScryRequest());

        await Assert.That(exception.Message).Contains(mentions);
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));
}
