/// <summary>
/// An in-memory source answers a string function under LINQ to Objects, where the culture-sensitive
/// members read the current culture — which request localization sets per request. The same wire
/// request then answered differently per Accept-Language; a relational source answers by its
/// collation whatever the culture. The in-memory members are bound ordinally now, and each case here
/// runs under a culture that would have answered otherwise.
/// </summary>
public class PocoCultureTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record NameRow(string Name);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task CasingDoesNotFollowTheRequestsCulture()
    {
        using var _ = Under("tr-TR");
        await using var context = TestContext.CreateSeeded();
        var client = new ScryClient((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));

        // Turkish casing sends i to a dotted capital İ, so a culture-sensitive ToUpper of "Christmas"
        // would have read as "CHRİSTMAS".
        var rows = await client.Source<Holiday>("Holiday")
            .Select(_ => new NameRow(_.Name.ToUpper()))
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Name)).Contains("CHRISTMAS");
    }

    [Test]
    public async Task APrefixDoesNotFollowTheRequestsCulture()
    {
        using var _ = Under("en-US");

        // A soft hyphen is ignorable to a culture-sensitive comparison and a character to an ordinal
        // one, so the culture would have found the prefix.
        var names = await Names(_ => _.Name.StartsWith("Chr\u00ADist"));

        await Assert.That(names).IsEmpty();
    }

    [Test]
    public async Task AThreeWayComparisonDoesNotFollowTheRequestsCulture()
    {
        using var _ = Under("en-US");

        // Ordinally a capital sorts before every lowercase letter, so each name is below "christmas";
        // the culture puts "christmas" first and would have found none.
        var names = await Names(_ => _.Name.CompareTo("christmas") < 0);

        await Assert.That(names).Count().IsEqualTo(3);
    }

    // Ordering and Min read text the way CompareTo does: ordinally, where a capital sorts before every
    // lowercase letter. The culture would put "apple" first, and both would disagree with a CompareTo
    // that put "Banana" below it.
    [Test]
    public async Task OrderingTextAgreesWithCompareTo()
    {
        using var _ = Under("en-US");
        var processor = ScryProcessor.Create<TestContext>(
            options => options.AddPocoSource<Holiday>(
                _ =>
                [
                    new() { Name = "apple" },
                    new() { Name = "Banana" },
                    new() { Name = "cherry" }
                ]));
        await using var context = TestContext.CreateSeeded();
        var client = new ScryClient((request, _) => Task.FromResult(processor.Execute(request, context)));
        var holidays = client.Source<Holiday>("Holiday");

        var ordered = await holidays.OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync();
        var least = await holidays.MinAsync(_ => _.Name);
        var below = await holidays.Where(_ => _.Name.CompareTo("apple") < 0).Select(_ => new NameRow(_.Name)).ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(ordered.Select(_ => _.Name)).IsEquivalentTo(["Banana", "apple", "cherry"], CollectionOrdering.Matching);
            await Assert.That(least).IsEqualTo("Banana");
            await Assert.That(below.Select(_ => _.Name)).IsEquivalentTo(["Banana"]);
        }
    }

    // The conversions between text and a value read the current culture under LINQ to Objects, where
    // the provider's CONVERT reads none. German separates decimals with a comma and groups with a dot,
    // so a culture-sensitive parse would have read "1.5" as fifteen.
    [Test]
    public async Task ParsingTextDoesNotFollowTheRequestsCulture()
    {
        using var _ = Under("de-DE");
        await using var context = TestContext.CreateSeeded();

        // A holiday holds no numeric text, so the text is a constant; the row the predicate is built
        // over still decides which source the function runs in.
        var request = QueryRequest.Create(
            "Holiday",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.LessThan,
                        new CallNode(KnownFunction.DecimalFrom, new ConstNode("1.5", ClrTypeTag.String), []),
                        new ConstNode("2", ClrTypeTag.Decimal))),
                new CountOp()
            ]);

        var response = SharedProcessor.Instance.Execute(request, context);

        await Assert.That(response.Payload.GetInt32()).IsEqualTo(3).Because("1.5 is below 2; read as fifteen, nothing would be");
    }

    // German writes a date with dots, day first, so a culture-sensitive ToString would have read
    // "01.01.2026".
    [Test]
    public async Task PrintingAValueDoesNotFollowTheRequestsCulture()
    {
        using var _ = Under("de-DE");
        await using var context = TestContext.CreateSeeded();
        var client = new ScryClient((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));

        var rows = await client.Source<Holiday>("Holiday")
            .Where(_ => _.Name == "New Year")
            .Select(_ => new NameRow(_.Date.ToString()))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("01/01/2026");
    }

    static async Task<List<string>> Names(Expression<Func<Holiday, bool>> predicate)
    {
        await using var context = TestContext.CreateSeeded();
        var client = new ScryClient((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));

        var rows = await client.Source<Holiday>("Holiday")
            .Where(predicate)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        return rows.Select(_ => _.Name).ToList();
    }

    // The culture request localization would have set, restored on the way out.
    static Restore Under(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new(culture);
        return new Restore(previous);
    }

    sealed class Restore(CultureInfo previous) :
        IDisposable
    {
        public void Dispose() =>
            CultureInfo.CurrentCulture = previous;
    }
}
