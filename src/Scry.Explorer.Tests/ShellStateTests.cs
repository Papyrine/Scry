// The state behind the shell: the tabs, the pane splits, and the namespaced storage both persist to.
public class ShellStateTests
{
    [Test]
    public async Task OpensOnOneTabCarryingTheSeededQuery()
    {
        var tabs = new TabStore("Query.Employee");

        await Assert.That(tabs.Tabs).Count().IsEqualTo(1);
        await Assert.That(tabs.Active.Query).IsEqualTo("Query.Employee");
    }

    [Test]
    public async Task ActivatesANewTab()
    {
        var tabs = new TabStore("first");
        tabs.Add("second");

        await Assert.That(tabs.ActiveIndex).IsEqualTo(1);
        await Assert.That(tabs.Active.Query).IsEqualTo("second");
    }

    // An explorer with no tab has nowhere to type.
    // Clearing the stored data resets the tabs with it: the keys alone being removed left the open
    // tabs in memory, and the next save wrote them straight back.
    [Test]
    public async Task ResetsToOneTabCarryingTheSeededQuery()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Add("Query.Department");
        tabs.Add("Query.Order");
        tabs.Rename(0, "First");

        tabs.Reset("Query.Employee");

        await Assert.That(tabs.Tabs).Count().IsEqualTo(1);
        await Assert.That(tabs.Active.Query).IsEqualTo("Query.Employee");
        await Assert.That(tabs.Active.Title).IsNull();
        await Assert.That(tabs.ActiveIndex).IsZero();
    }

    [Test]
    public async Task RefusesToCloseTheLastTab()
    {
        var tabs = new TabStore("only");
        tabs.Close(0);

        await Assert.That(tabs.Tabs).Count().IsEqualTo(1);
    }

    [Test]
    public async Task KeepsTheActiveTabWhenAnEarlierOneCloses()
    {
        var tabs = new TabStore("first");
        tabs.Add("second");
        tabs.Add("third");

        tabs.Close(0);

        await Assert.That(tabs.Active.Query).IsEqualTo("third");
    }

    [Test]
    public async Task ClampsTheActiveIndexWhenTheLastTabCloses()
    {
        var tabs = new TabStore("first");
        tabs.Add("second");

        tabs.Close(1);

        await Assert.That(tabs.ActiveIndex).IsZero();
        await Assert.That(tabs.Active.Query).IsEqualTo("first");
    }

    // The source is what distinguishes two tabs in practice.
    [Test]
    [Arguments("Query.Employee.Where(_ => _.Active)", "Employee")]
    [Arguments("Query.EmployeeSummary", "EmployeeSummary")]
    [Arguments("var since = new DateOnly(2026, 1, 1);\nQuery.Order", "Order")]
    [Arguments("Query.Employee\n    .Select(_ => new { _.Name })", "Employee")]
    public async Task DerivesATitleFromTheSource(string query, string expected) =>
        await Assert.That(TabStore.SourceOf(query)).IsEqualTo(expected);

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("Employee.Where(_ => _.Active)")]
    [Arguments("Query.")]
    public async Task DerivesNoTitleWithoutASource(string query) =>
        await Assert.That(TabStore.SourceOf(query)).IsNull();

    [Test]
    public async Task NumbersATabWithNoSourceToNameIt()
    {
        var tabs = new TabStore();

        await Assert.That(tabs.Title(tabs.Active)).IsEqualTo("Query 1");
    }

    [Test]
    public async Task PrefersATypedTitleOverTheDerivedOne()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Rename(0, "Active staff");

        await Assert.That(tabs.Title(tabs.Active)).IsEqualTo("Active staff");
    }

    [Test]
    public async Task TreatsABlankRenameAsNone()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Rename(0, "   ");

        await Assert.That(tabs.Title(tabs.Active)).IsEqualTo("Employee");
    }

    [Test]
    public async Task RoundTripsTabsThroughStorage()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Add("Query.Order");
        tabs.Rename(0, "Staff");

        var loaded = new TabStore();
        loaded.Load(tabs.Serialize());

        await Assert.That(loaded.Tabs).Count().IsEqualTo(2);
        await Assert.That(loaded.ActiveIndex).IsEqualTo(1);
        await Assert.That(loaded.Title(loaded.Tabs[0])).IsEqualTo("Staff");
        await Assert.That(loaded.Tabs[1].Query).IsEqualTo("Query.Order");
    }

    // Two windows of the explorer on one origin write the same key. Before a window writes, it adopts
    // the tabs the other wrote since it last read, so a save carries both windows' tabs rather than
    // overwriting the other's with only its own.
    [Test]
    public async Task AdoptsTheTabsAnotherWindowWrote()
    {
        var mine = new TabStore("Query.Employee");
        var theirs = new TabStore("Query.Employee");
        theirs.Load(mine.Serialize());
        theirs.Add("Query.Department");

        var adopted = mine.Merge(theirs.Serialize());

        await Assert.That(adopted).IsTrue();
        await Assert.That(mine.Tabs.Select(_ => _.Query)).IsEquivalentTo(["Query.Employee", "Query.Department"], CollectionOrdering.Matching);
        await Assert.That(mine.ActiveIndex).IsZero();
        await Assert.That(mine.Merge(theirs.Serialize())).IsFalse().Because("adopted once");
    }

    // A tab closed here is not the other window's to reopen: what it holds is a tab this window held
    // and let go of, which is a decision the merge respects.
    [Test]
    public async Task DoesNotReadoptATabClosedHere()
    {
        var mine = new TabStore("Query.Employee");
        mine.Add("Query.Department");
        var written = mine.Serialize();
        mine.Close(1);

        await Assert.That(mine.Merge(written)).IsFalse();
        await Assert.That(mine.Tabs).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("{\"tabs\":[null]}")]
    public async Task AdoptsNothingFromAValueItCannotRead(string? json)
    {
        var mine = new TabStore("Query.Employee");

        await Assert.That(mine.Merge(json)).IsFalse();
        await Assert.That(mine.Tabs).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("{\"tabs\":[]}")]
    [Arguments("{\"tabs\":null}")]
    [Arguments("{\"tabs\":[null]}")]
    public async Task KeepsTheOpenTabOnAValueItCannotRead(string? json)
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Load(json);

        await Assert.That(tabs.Tabs).Count().IsEqualTo(1);
        await Assert.That(tabs.Active.Query).IsEqualTo("Query.Employee");
    }

    // Tab by tab: a null where a tab should be is dropped, a tab missing its text is a blank one, and
    // a tab missing its id is given one. Each of these failed the first render before the button that
    // clears the storage could be reached.
    [Test]
    public async Task ReadsTheTabsItCanBesideOnesItCannot()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Load("{\"tabs\":[null,{\"id\":null,\"query\":null},{\"query\":\"Query.Region\"}],\"activeIndex\":2}");

        await Assert.That(tabs.Tabs.Select(_ => _.Query)).IsEquivalentTo(["", "Query.Region"], CollectionOrdering.Matching);
        await Assert.That(tabs.Tabs[0].Id).IsNotEmpty();
        await Assert.That(tabs.Title(tabs.Tabs[0])).IsEqualTo("Query 1");
        await Assert.That(tabs.Active.Query).IsEqualTo("Query.Region");
    }

    // A pane dragged past either end keeps a usable sliver rather than vanishing into an edge that
    // cannot be grabbed again.
    [Test]
    public async Task ClampsADragToThePanesLimits()
    {
        var pane = new PaneState(0.5, 0.2, 0.8);

        pane.Drag(0.95);
        await Assert.That(pane.Ratio).IsEqualTo(0.8);

        pane.Drag(0.01);
        await Assert.That(pane.Ratio).IsEqualTo(0.2);
    }

    [Test]
    public async Task ResetsToTheDefaultSplit()
    {
        var pane = new PaneState(0.5);
        pane.Drag(0.7);

        pane.Reset();

        await Assert.That(pane.Ratio).IsEqualTo(0.5);
    }

    // The ratio is written straight into a style attribute, so it must not pick up a comma from the
    // machine's own number format.
    [Test]
    public async Task WritesTheGrowStyleInvariantly()
    {
        var pane = new PaneState(0.5);
        pane.Drag(0.625);

        await Assert.That(pane.Grow()).IsEqualTo("flex: 0.625 1 0%");
    }

    [Test]
    public async Task RoundTripsAPaneRatio()
    {
        var pane = new PaneState(0.5);
        pane.Drag(0.625);

        var loaded = new PaneState(0.5);
        loaded.Load(pane.Serialize());

        await Assert.That(loaded.Ratio).IsEqualTo(0.625);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("collapsed")]
    [Arguments("NaN")]
    [Arguments("Infinity")]
    public async Task FallsBackToTheDefaultForAStoredRatioItCannotRead(string? stored)
    {
        var pane = new PaneState(0.4);
        pane.Drag(0.7);

        pane.Load(stored);

        await Assert.That(pane.Ratio).IsEqualTo(0.4);
    }

    [Test]
    public async Task StoresUnderTheNamespace()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);

        storage.Set("tabs", "value");

        await Assert.That(backend.Get("scry:tabs")).IsEqualTo("value");
        await Assert.That(storage.Get("tabs")).IsEqualTo("value");
    }

    [Test]
    public async Task RemovesAKeySetToEmpty()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);
        storage.Set("plugin", "Schema");

        storage.Set("plugin", "");

        await Assert.That(storage.Get("plugin")).IsNull();
    }

    // A literal "null"/"undefined" is a serialization accident from a previous session.
    [Test]
    [Arguments("null")]
    [Arguments("undefined")]
    public async Task HealsACorruptSlot(string stored)
    {
        var backend = new InMemoryStorageBackend();
        backend.Set("scry:tabs", stored);
        var storage = new StorageService(backend);

        await Assert.That(storage.Get("tabs")).IsNull();
        await Assert.That(backend.Get("scry:tabs")).IsNull();
    }

    [Test]
    public async Task ClearsOnlyItsOwnNamespace()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);
        storage.Set("tabs", "value");
        storage.Set("plugin", "Schema");
        backend.Set("someone-elses-key", "keep me");
        backend.Set("scry-theme", "dark");

        storage.Clear();

        await Assert.That(backend.Get("scry:tabs")).IsNull();
        await Assert.That(backend.Get("scry:plugin")).IsNull();
        await Assert.That(backend.Get("someone-elses-key")).IsEqualTo("keep me");

        // The theme sits outside the namespace, so Clear does not reach it — the explorer removes it
        // separately, which is the only reason RawRemove exists.
        await Assert.That(backend.Get("scry-theme")).IsEqualTo("dark");
    }

    [Test]
    public async Task ReadsAndWritesOutsideTheNamespace()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);

        storage.RawSet("scry-theme", "dark");

        await Assert.That(backend.Get("scry-theme")).IsEqualTo("dark");
        await Assert.That(storage.RawGet("scry-theme")).IsEqualTo("dark");

        storage.RawRemove("scry-theme");
        await Assert.That(storage.RawGet("scry-theme")).IsNull();
    }
}
