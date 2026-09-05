using System.Linq.Expressions;

/// <summary>
/// An in-memory source answers a string function under LINQ to Objects, where the culture-sensitive
/// members read the current culture — which request localization sets per request. The same wire
/// request then answered differently per Accept-Language; a relational source answers by its
/// collation whatever the culture. The in-memory members are bound ordinally now, and each case here
/// runs under a culture that would have answered otherwise.
/// </summary>
[TestFixture]
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

        Assert.That(rows.Select(_ => _.Name), Does.Contain("CHRISTMAS"));
    }

    [Test]
    public async Task APrefixDoesNotFollowTheRequestsCulture()
    {
        using var _ = Under("en-US");

        // A soft hyphen is ignorable to a culture-sensitive comparison and a character to an ordinal
        // one, so the culture would have found the prefix.
        var names = await Names(_ => _.Name.StartsWith("Chr\u00ADist"));

        Assert.That(names, Is.Empty);
    }

    [Test]
    public async Task AThreeWayComparisonDoesNotFollowTheRequestsCulture()
    {
        using var _ = Under("en-US");

        // Ordinally a capital sorts before every lowercase letter, so each name is below "christmas";
        // the culture puts "christmas" first and would have found none.
        var names = await Names(_ => _.Name.CompareTo("christmas") < 0);

        Assert.That(names, Has.Count.EqualTo(3));
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
    static IDisposable Under(string culture)
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
