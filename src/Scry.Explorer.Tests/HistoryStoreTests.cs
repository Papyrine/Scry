// The queries the explorer remembers. The flattening rule below is load-bearing beyond the pane: the
// docs screenshot asserts the rendered entry, so a change to it changes a published image.
[TestFixture]
public class HistoryStoreTests
{
    [Test]
    public void RecordsNewestFirst()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");

        Assert.That(store.Items.Select(_ => _.Query), Is.EqualTo(new[] { "two", "one" }));
    }

    [Test]
    public void IgnoresABlankQuery()
    {
        var store = new HistoryStore();
        store.Add("   ");

        Assert.That(store.Count, Is.Zero);
    }

    // An exact repeat moves the existing entry up rather than adding a second, so whatever was
    // attached to it survives.
    [Test]
    public void MovesARepeatUpAndKeepsItsLabel()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.SetLabel("one", "My query");
        store.Add("two");
        store.Add("one");

        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(store.Items[0].Query, Is.EqualTo("one"));
        Assert.That(store.Items[0].Label, Is.EqualTo("My query"));
    }

    [Test]
    public void CapsOrdinaryEntries()
    {
        var store = new HistoryStore();
        for (var index = 0; index < HistoryStore.MaxItems + 5; index++)
        {
            store.Add($"query {index}");
        }

        Assert.That(store.Count, Is.EqualTo(HistoryStore.MaxItems));

        // The oldest went, the newest stayed.
        Assert.That(store.Items[0].Query, Is.EqualTo($"query {HistoryStore.MaxItems + 4}"));
        Assert.That(store.Items.Select(_ => _.Query), Does.Not.Contain("query 0"));
    }

    // A favorite is a deliberate keep: it neither occupies a slot under the cap nor is evicted from one.
    [Test]
    public void NeverEvictsAFavorite()
    {
        var store = new HistoryStore();
        store.Add("keeper");
        store.SetFavorite("keeper", true);
        for (var index = 0; index < HistoryStore.MaxItems + 5; index++)
        {
            store.Add($"query {index}");
        }

        Assert.That(store.Items.Select(_ => _.Query), Does.Contain("keeper"));
        Assert.That(store.Count, Is.EqualTo(HistoryStore.MaxItems + 1));
    }

    [Test]
    public void ListsFavoritesFirst()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");
        store.Add("three");
        store.SetFavorite("one", true);

        Assert.That(store.Items[0].Query, Is.EqualTo("one"));
    }

    [Test]
    public void PutsAnUnmarkedFavoriteBackUnderTheCap()
    {
        var store = new HistoryStore();
        store.Add("keeper");
        store.SetFavorite("keeper", true);
        for (var index = 0; index < HistoryStore.MaxItems; index++)
        {
            store.Add($"query {index}");
        }

        store.SetFavorite("keeper", false);

        Assert.That(store.Count, Is.EqualTo(HistoryStore.MaxItems));
        Assert.That(store.Items.Select(_ => _.Query), Does.Not.Contain("keeper"));
    }

    // Losing a favorite to Clear is not recoverable, so Clear does not take them.
    [Test]
    public void ClearKeepsFavorites()
    {
        var store = new HistoryStore();
        store.Add("ordinary");
        store.Add("keeper");
        store.SetFavorite("keeper", true);

        store.Clear();

        Assert.That(store.Items.Select(_ => _.Query), Is.EqualTo(new[] { "keeper" }));
    }

    [Test]
    public void RemovesByText()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");

        store.Remove("one");

        Assert.That(store.Items.Select(_ => _.Query), Is.EqualTo(new[] { "two" }));
    }

    [Test]
    public void TreatsABlankLabelAsNone()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.SetLabel("one", "   ");

        Assert.That(store.Items[0].Label, Is.Null);
    }

    // A multi-line query reads as the fluent chain it is: a continuation line is appended directly, so
    // its indentation does not survive as stray spaces before every operator.
    [Test]
    public void FlattensAFluentChainWithoutStraySpaces() =>
        Assert.That(
            HistoryStore.Flatten(
                """
                Query.Employee
                    .Where(_ => _.Active)
                    .Select(_ => new { _.Name })
                """),
            Is.EqualTo("Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name })"));

    [Test]
    public void FlattensSeparateStatementsWithASpace() =>
        Assert.That(
            HistoryStore.Flatten(
                """
                var since = new DateOnly(2026, 1, 1);
                Query.Employee
                """),
            Is.EqualTo("var since = new DateOnly(2026, 1, 1); Query.Employee"));

    [Test]
    public void ShowsTheLabelInsteadOfTheQueryWhenThereIsOne()
    {
        var store = new HistoryStore();
        store.Add("Query.Employee");
        store.SetLabel("Query.Employee", "Everyone");

        Assert.That(HistoryStore.DisplayText(store.Items[0]), Is.EqualTo("Everyone"));
    }

    // Both spellings are searched, so an entry found by either is found.
    [TestCase("Employee", true)]
    [TestCase("employee", true)]
    [TestCase("Everyone", true)]
    [TestCase("Department", false)]
    [TestCase("", true)]
    [TestCase(null, true)]
    public void MatchesLabelAndQuery(string? filter, bool expected)
    {
        var item = new HistoryItem
        {
            Query = "Query.Employee",
            Label = "Everyone"
        };

        Assert.That(HistoryStore.Matches(item, filter), Is.EqualTo(expected));
    }

    [Test]
    public void RoundTripsThroughStorage()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");
        store.SetLabel("one", "First");
        store.SetFavorite("one", true);

        var loaded = new HistoryStore();
        loaded.Load(store.Serialize());

        Assert.That(loaded.Items[0].Query, Is.EqualTo("one"));
        Assert.That(loaded.Items[0].Label, Is.EqualTo("First"));
        Assert.That(loaded.Items[0].Favorite);
        Assert.That(loaded.Count, Is.EqualTo(2));
    }

    // Corrupt or from a shape this version does not read: start empty rather than fail the page.
    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{\"not\":\"an array\"}")]
    public void StartsEmptyOnAValueItCannotRead(string? json)
    {
        var store = new HistoryStore();
        store.Load(json);

        Assert.That(store.Count, Is.Zero);
    }

    // The value written before entries carried labels: a plain array of query strings.
    [Test]
    public void AdoptsTheLegacyShape()
    {
        var store = new HistoryStore();
        store.LoadLegacy("""["two","one"]""");

        Assert.That(store.Items.Select(_ => _.Query), Is.EqualTo(new[] { "two", "one" }));
        Assert.That(store.Items.All(_ => _.Label is null));
        Assert.That(store.Items.All(_ => !_.Favorite));
    }

    [Test]
    public void CapsTheLegacyShapeToo()
    {
        var store = new HistoryStore();
        store.LoadLegacy(
            JsonSerializer.Serialize(
                Enumerable.Range(0, HistoryStore.MaxItems + 5).Select(_ => $"query {_}")));

        Assert.That(store.Count, Is.EqualTo(HistoryStore.MaxItems));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    public void StartsEmptyOnALegacyValueItCannotRead(string? json)
    {
        var store = new HistoryStore();
        store.LoadLegacy(json);

        Assert.That(store.Count, Is.Zero);
    }
}
