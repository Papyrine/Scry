// The state behind the shell: the tabs, the pane splits, and the namespaced storage both persist to.
[TestFixture]
public class ShellStateTests
{
    [Test]
    public void OpensOnOneTabCarryingTheSeededQuery()
    {
        var tabs = new TabStore("Query.Employee");

        Assert.That(tabs.Tabs, Has.Count.EqualTo(1));
        Assert.That(tabs.Active.Query, Is.EqualTo("Query.Employee"));
    }

    [Test]
    public void ActivatesANewTab()
    {
        var tabs = new TabStore("first");
        tabs.Add("second");

        Assert.That(tabs.ActiveIndex, Is.EqualTo(1));
        Assert.That(tabs.Active.Query, Is.EqualTo("second"));
    }

    // An explorer with no tab has nowhere to type.
    [Test]
    public void RefusesToCloseTheLastTab()
    {
        var tabs = new TabStore("only");
        tabs.Close(0);

        Assert.That(tabs.Tabs, Has.Count.EqualTo(1));
    }

    [Test]
    public void KeepsTheActiveTabWhenAnEarlierOneCloses()
    {
        var tabs = new TabStore("first");
        tabs.Add("second");
        tabs.Add("third");

        tabs.Close(0);

        Assert.That(tabs.Active.Query, Is.EqualTo("third"));
    }

    [Test]
    public void ClampsTheActiveIndexWhenTheLastTabCloses()
    {
        var tabs = new TabStore("first");
        tabs.Add("second");

        tabs.Close(1);

        Assert.That(tabs.ActiveIndex, Is.Zero);
        Assert.That(tabs.Active.Query, Is.EqualTo("first"));
    }

    // The source is what distinguishes two tabs in practice.
    [TestCase("Query.Employee.Where(_ => _.Active)", "Employee")]
    [TestCase("Query.EmployeeSummary", "EmployeeSummary")]
    [TestCase("var since = new DateOnly(2026, 1, 1);\nQuery.Order", "Order")]
    [TestCase("Query.Employee\n    .Select(_ => new { _.Name })", "Employee")]
    public void DerivesATitleFromTheSource(string query, string expected) =>
        Assert.That(TabStore.SourceOf(query), Is.EqualTo(expected));

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Employee.Where(_ => _.Active)")]
    [TestCase("Query.")]
    public void DerivesNoTitleWithoutASource(string query) =>
        Assert.That(TabStore.SourceOf(query), Is.Null);

    [Test]
    public void NumbersATabWithNoSourceToNameIt()
    {
        var tabs = new TabStore();

        Assert.That(tabs.Title(tabs.Active), Is.EqualTo("Query 1"));
    }

    [Test]
    public void PrefersATypedTitleOverTheDerivedOne()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Rename(0, "Active staff");

        Assert.That(tabs.Title(tabs.Active), Is.EqualTo("Active staff"));
    }

    [Test]
    public void TreatsABlankRenameAsNone()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Rename(0, "   ");

        Assert.That(tabs.Title(tabs.Active), Is.EqualTo("Employee"));
    }

    [Test]
    public void RoundTripsTabsThroughStorage()
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Add("Query.Order");
        tabs.Rename(0, "Staff");

        var loaded = new TabStore();
        loaded.Load(tabs.Serialize());

        Assert.That(loaded.Tabs, Has.Count.EqualTo(2));
        Assert.That(loaded.ActiveIndex, Is.EqualTo(1));
        Assert.That(loaded.Title(loaded.Tabs[0]), Is.EqualTo("Staff"));
        Assert.That(loaded.Tabs[1].Query, Is.EqualTo("Query.Order"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("{\"tabs\":[]}")]
    public void KeepsTheOpenTabOnAValueItCannotRead(string? json)
    {
        var tabs = new TabStore("Query.Employee");
        tabs.Load(json);

        Assert.That(tabs.Tabs, Has.Count.EqualTo(1));
        Assert.That(tabs.Active.Query, Is.EqualTo("Query.Employee"));
    }

    // A pane dragged past either end keeps a usable sliver rather than vanishing into an edge that
    // cannot be grabbed again.
    [Test]
    public void ClampsADragToThePanesLimits()
    {
        var pane = new PaneState(0.5, 0.2, 0.8);

        pane.Drag(0.95);
        Assert.That(pane.Ratio, Is.EqualTo(0.8));

        pane.Drag(0.01);
        Assert.That(pane.Ratio, Is.EqualTo(0.2));
    }

    [Test]
    public void ResetsToTheDefaultSplit()
    {
        var pane = new PaneState(0.5);
        pane.Drag(0.7);

        pane.Reset();

        Assert.That(pane.Ratio, Is.EqualTo(0.5));
    }

    // The ratio is written straight into a style attribute, so it must not pick up a comma from the
    // machine's own number format.
    [Test]
    public void WritesTheGrowStyleInvariantly()
    {
        var pane = new PaneState(0.5);
        pane.Drag(0.625);

        Assert.That(pane.Grow(), Is.EqualTo("flex: 0.625 1 0%"));
    }

    [Test]
    public void RoundTripsAPaneRatio()
    {
        var pane = new PaneState(0.5);
        pane.Drag(0.625);

        var loaded = new PaneState(0.5);
        loaded.Load(pane.Serialize());

        Assert.That(loaded.Ratio, Is.EqualTo(0.625));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("collapsed")]
    [TestCase("NaN")]
    [TestCase("Infinity")]
    public void FallsBackToTheDefaultForAStoredRatioItCannotRead(string? stored)
    {
        var pane = new PaneState(0.4);
        pane.Drag(0.7);

        pane.Load(stored);

        Assert.That(pane.Ratio, Is.EqualTo(0.4));
    }

    [Test]
    public void StoresUnderTheNamespace()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);

        storage.Set("tabs", "value");

        Assert.That(backend.Get("scry:tabs"), Is.EqualTo("value"));
        Assert.That(storage.Get("tabs"), Is.EqualTo("value"));
    }

    [Test]
    public void RemovesAKeySetToEmpty()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);
        storage.Set("plugin", "Schema");

        storage.Set("plugin", "");

        Assert.That(storage.Get("plugin"), Is.Null);
    }

    // A literal "null"/"undefined" is a serialization accident from a previous session.
    [TestCase("null")]
    [TestCase("undefined")]
    public void HealsACorruptSlot(string stored)
    {
        var backend = new InMemoryStorageBackend();
        backend.Set("scry:tabs", stored);
        var storage = new StorageService(backend);

        Assert.That(storage.Get("tabs"), Is.Null);
        Assert.That(backend.Get("scry:tabs"), Is.Null);
    }

    [Test]
    public void ClearsOnlyItsOwnNamespace()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);
        storage.Set("tabs", "value");
        storage.Set("plugin", "Schema");
        backend.Set("someone-elses-key", "keep me");
        backend.Set("scry-theme", "dark");

        storage.Clear();

        Assert.That(backend.Get("scry:tabs"), Is.Null);
        Assert.That(backend.Get("scry:plugin"), Is.Null);
        Assert.That(backend.Get("someone-elses-key"), Is.EqualTo("keep me"));

        // The theme sits outside the namespace, so Clear does not reach it — the explorer removes it
        // separately, which is the only reason RawRemove exists.
        Assert.That(backend.Get("scry-theme"), Is.EqualTo("dark"));
    }

    [Test]
    public void ReadsAndWritesOutsideTheNamespace()
    {
        var backend = new InMemoryStorageBackend();
        var storage = new StorageService(backend);

        storage.RawSet("scry-theme", "dark");

        Assert.That(backend.Get("scry-theme"), Is.EqualTo("dark"));
        Assert.That(storage.RawGet("scry-theme"), Is.EqualTo("dark"));

        storage.RawRemove("scry-theme");
        Assert.That(storage.RawGet("scry-theme"), Is.Null);
    }
}
