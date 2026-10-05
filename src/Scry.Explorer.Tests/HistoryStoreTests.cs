// The queries the explorer remembers. The flattening rule below is load-bearing beyond the pane: the
// docs screenshot asserts the rendered entry, so a change to it changes a published image.
public class HistoryStoreTests
{
    [Test]
    public async Task RecordsNewestFirst()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");

        await Assert.That(store.Items.Select(_ => _.Query)).IsEquivalentTo(["two", "one"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task IgnoresABlankQuery()
    {
        var store = new HistoryStore();
        store.Add("   ");

        await Assert.That(store.Count).IsZero();
    }

    // An exact repeat moves the existing entry up rather than adding a second, so whatever was
    // attached to it survives.
    [Test]
    public async Task MovesARepeatUpAndKeepsItsLabel()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.SetLabel("one", "My query");
        store.Add("two");
        store.Add("one");

        await Assert.That(store.Count).IsEqualTo(2);
        await Assert.That(store.Items[0].Query).IsEqualTo("one");
        await Assert.That(store.Items[0].Label).IsEqualTo("My query");
    }

    [Test]
    public async Task CapsOrdinaryEntries()
    {
        var store = new HistoryStore();
        for (var index = 0; index < HistoryStore.MaxItems + 5; index++)
        {
            store.Add($"query {index}");
        }

        await Assert.That(store.Count).IsEqualTo(HistoryStore.MaxItems);

        // The oldest went, the newest stayed.
        await Assert.That(store.Items[0].Query).IsEqualTo($"query {HistoryStore.MaxItems + 4}");
        await Assert.That(store.Items.Select(_ => _.Query)).DoesNotContain("query 0");
    }

    // A favorite is a deliberate keep: it neither occupies a slot under the cap nor is evicted from one.
    [Test]
    public async Task NeverEvictsAFavorite()
    {
        var store = new HistoryStore();
        store.Add("keeper");
        store.SetFavorite("keeper", true);
        for (var index = 0; index < HistoryStore.MaxItems + 5; index++)
        {
            store.Add($"query {index}");
        }

        await Assert.That(store.Items.Select(_ => _.Query)).Contains("keeper");
        await Assert.That(store.Count).IsEqualTo(HistoryStore.MaxItems + 1);
    }

    [Test]
    public async Task ListsFavoritesFirst()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");
        store.Add("three");
        store.SetFavorite("one", true);

        await Assert.That(store.Items[0].Query).IsEqualTo("one");
    }

    [Test]
    public async Task PutsAnUnmarkedFavoriteBackUnderTheCap()
    {
        var store = new HistoryStore();
        store.Add("keeper");
        store.SetFavorite("keeper", true);
        for (var index = 0; index < HistoryStore.MaxItems; index++)
        {
            store.Add($"query {index}");
        }

        store.SetFavorite("keeper", false);

        await Assert.That(store.Count).IsEqualTo(HistoryStore.MaxItems);
        await Assert.That(store.Items.Select(_ => _.Query)).DoesNotContain("keeper");
    }

    // Losing a favorite to Clear is not recoverable, so Clear does not take them.
    [Test]
    public async Task ClearKeepsFavorites()
    {
        var store = new HistoryStore();
        store.Add("ordinary");
        store.Add("keeper");
        store.SetFavorite("keeper", true);

        store.Clear();

        await Assert.That(store.Items.Select(_ => _.Query)).IsEquivalentTo(["keeper"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RemovesByText()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");

        store.Remove("one");

        await Assert.That(store.Items.Select(_ => _.Query)).IsEquivalentTo(["two"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TreatsABlankLabelAsNone()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.SetLabel("one", "   ");

        await Assert.That(store.Items[0].Label).IsNull();
    }

    // A multi-line query reads as the fluent chain it is: a continuation line is appended directly, so
    // its indentation does not survive as stray spaces before every operator.
    [Test]
    public async Task FlattensAFluentChainWithoutStraySpaces() =>
        await Assert.That(HistoryStore.Flatten(
                """
                Query.Employee
                    .Where(_ => _.Active)
                    .Select(_ => new { _.Name })
                """))
            .IsEqualTo("Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name })");

    [Test]
    public async Task FlattensSeparateStatementsWithASpace() =>
        await Assert.That(HistoryStore.Flatten(
                """
                var since = new DateOnly(2026, 1, 1);
                Query.Employee
                """))
            .IsEqualTo("var since = new DateOnly(2026, 1, 1); Query.Employee");

    [Test]
    public async Task ShowsTheLabelInsteadOfTheQueryWhenThereIsOne()
    {
        var store = new HistoryStore();
        store.Add("Query.Employee");
        store.SetLabel("Query.Employee", "Everyone");

        await Assert.That(HistoryStore.DisplayText(store.Items[0])).IsEqualTo("Everyone");
    }

    // Both spellings are searched, so an entry found by either is found.
    [Test]
    [Arguments("Employee", true)]
    [Arguments("employee", true)]
    [Arguments("Everyone", true)]
    [Arguments("Department", false)]
    [Arguments("", true)]
    [Arguments(null, true)]
    public async Task MatchesLabelAndQuery(string? filter, bool expected)
    {
        var item = new HistoryItem
        {
            Query = "Query.Employee",
            Label = "Everyone"
        };

        await Assert.That(HistoryStore.Matches(item, filter)).IsEqualTo(expected);
    }

    [Test]
    public async Task RoundTripsThroughStorage()
    {
        var store = new HistoryStore();
        store.Add("one");
        store.Add("two");
        store.SetLabel("one", "First");
        store.SetFavorite("one", true);

        var loaded = new HistoryStore();
        loaded.Load(store.Serialize());

        await Assert.That(loaded.Items[0].Query).IsEqualTo("one");
        await Assert.That(loaded.Items[0].Label).IsEqualTo("First");
        await Assert.That(loaded.Items[0].Favorite).IsTrue();
        await Assert.That(loaded.Count).IsEqualTo(2);
    }

    // Corrupt or from a shape this version does not read: start empty rather than fail the page. The
    // last two parse, and each held an entry that failed the first render before the button that
    // clears the storage could be reached.
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("{\"not\":\"an array\"}")]
    [Arguments("[null]")]
    [Arguments("[{\"query\":null}]")]
    public async Task StartsEmptyOnAValueItCannotRead(string? json)
    {
        var store = new HistoryStore();
        store.Load(json);

        await Assert.That(store.Count).IsZero();
    }

    // Entry by entry: the ones that read survive the ones that do not.
    [Test]
    public async Task KeepsTheEntriesItCanReadBesideOnesItCannot()
    {
        var store = new HistoryStore();
        store.Load("[null,{\"query\":\"Query.Employee\"},{\"query\":null}]");

        await Assert.That(store.Items.Select(_ => _.Query)).IsEquivalentTo(["Query.Employee"], CollectionOrdering.Matching);
    }

    // The value written before entries carried labels: a plain array of query strings.
    [Test]
    public async Task AdoptsTheLegacyShape()
    {
        var store = new HistoryStore();
        store.LoadLegacy("""["two","one"]""");

        await Assert.That(store.Items.Select(_ => _.Query)).IsEquivalentTo(["two", "one"], CollectionOrdering.Matching);
        await Assert.That(store.Items.All(_ => _.Label is null)).IsTrue();
        await Assert.That(store.Items.All(_ => !_.Favorite)).IsTrue();
    }

    [Test]
    public async Task CapsTheLegacyShapeToo()
    {
        var store = new HistoryStore();
        store.LoadLegacy(
            JsonSerializer.Serialize(
                Enumerable.Range(0, HistoryStore.MaxItems + 5).Select(_ => $"query {_}")));

        await Assert.That(store.Count).IsEqualTo(HistoryStore.MaxItems);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    public async Task StartsEmptyOnALegacyValueItCannotRead(string? json)
    {
        var store = new HistoryStore();
        store.LoadLegacy(json);

        await Assert.That(store.Count).IsZero();
    }
}
