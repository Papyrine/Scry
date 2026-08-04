/// <summary>
/// Surface adopted from EF's own translation set: the char overloads of the string functions,
/// <c>GetValueOrDefault</c> carried as the coalesce it abbreviates, and <c>AddMilliseconds</c>.
/// </summary>
[TestFixture]
public class ExpandedFunctionTests
{
    // A char constant travels under the String tag, so the char overloads reach the same wire
    // functions as their string forms and the server binds the string spelling.
    [Test]
    public async Task CharOverloadsTranslateLikeTheirStringForms()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Region.StartsWith('N') && _.Region.Contains('o') && _.Region.EndsWith('h'))
            .Select(_ => new {_.Region, Index = _.Region.IndexOf('o'), Masked = _.Region.Replace('o', '0')})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Select(_ => _.Region), Is.All.EqualTo("North"));
            Assert.That(rows.Select(_ => _.Index), Is.All.EqualTo(1));
            Assert.That(rows.Select(_ => _.Masked), Is.All.EqualTo("N0rth"));
        });
    }

    [Test]
    public async Task CoalescesAnOptionalMemberToItsDefault()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Select(_ => new {_.Amount, Discount = _.Discount.GetValueOrDefault()})
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows.Single(_ => _.Amount == 250m).Discount, Is.Zero);
            Assert.That(rows.Single(_ => _.Amount == 100m).Discount, Is.EqualTo(10m));
            Assert.That(rows.Single(_ => _.Amount == 75m).Discount, Is.EqualTo(5m));
        });
    }

    [Test]
    public async Task CoalescesAnOptionalMemberToAFallback()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Discount.GetValueOrDefault(100m) == 100m)
            .Select(_ => new {_.Amount})
            .ToListAsync();

        Assert.That(rows.Single().Amount, Is.EqualTo(250m));
    }

    [Test]
    public async Task AddsMilliseconds()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // One order is placed at 2025-12-31 23:59:59; a second later it is 2026.
        var unshifted = await client.Source<Order>("Order").CountAsync(_ => _.Placed.Year == 2026);
        var shifted = await client.Source<Order>("Order").CountAsync(_ => _.Placed.AddMilliseconds(1000).Year == 2026);

        Assert.Multiple(() =>
        {
            Assert.That(unshifted, Is.EqualTo(2));
            Assert.That(shifted, Is.EqualTo(3));
        });
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
