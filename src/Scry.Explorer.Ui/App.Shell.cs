using Microsoft.JSInterop;

namespace Scry;

// The shell around the editor: the tabs, the resizable panes, the plugin column, the dialogs, and the
// document-level shortcuts that reach the commands Monaco's own keymap cannot.
public partial class App
{
    /// <summary>
    /// The output views there is currently something to show in. A tab exists only when it has
    /// content, so a refused run leaves the column empty rather than offering three empty panes.
    /// </summary>
    IReadOnlyList<OutputTab> OpenTabs
    {
        get
        {
            var open = new List<OutputTab>(3);
            if (result is not null ||
                scalarResult is not null)
            {
                open.Add(OutputTab.Result);
            }

            if (resultJson is not null)
            {
                open.Add(OutputTab.Response);
            }

            if (sqlText is not null)
            {
                open.Add(OutputTab.Sql);
            }

            return open;
        }
    }

    // Selects a view that actually has something behind it: whatever the run produced, rather than
    // whichever tab the previous run left selected.
    void ShowOutput(OutputTab preferred)
    {
        var open = OpenTabs;
        if (open.Count == 0)
        {
            return;
        }

        activeOutput = open.Contains(preferred) ? preferred : open[0];
    }

    // ---- Tabs ----

    async Task ActivateTab(int index)
    {
        await StashActiveTab();
        tabs.Activate(index);
        await LoadActiveTab();
    }

    async Task AddTab()
    {
        await StashActiveTab();
        tabs.Add();
        await LoadActiveTab();
    }

    async Task CloseTab(int index)
    {
        var wasActive = index == tabs.ActiveIndex;
        tabs.Close(index);
        if (wasActive)
        {
            await LoadActiveTab();
        }

        SchedulePersist();
    }

    void RenameTab((int Index, string Title) rename)
    {
        tabs.Rename(rename.Index, rename.Title);
        SchedulePersist();
    }

    // One editor serves every tab, so switching means writing the outgoing tab's text back to it
    // first. The cost is that a tab's undo history does not survive the switch — the alternative is a
    // model per tab, and more than one editor on the page is what the completion providers are not
    // built for.
    async Task StashActiveTab()
    {
        if (editorReady)
        {
            tabs.Active.Query = await editor.GetValue();
        }
    }

    async Task LoadActiveTab()
    {
        // Each tab keeps its own output: switching to one shows what it last produced, not what the
        // tab being left produced.
        ClearOutput();
        if (editorReady)
        {
            await editor.SetValue(tabs.Active.Query);
            await MoveCaretToEnd();
        }

        SchedulePersist();
    }

    void ClearOutput()
    {
        wireJson = null;
        resultJson = null;
        result = null;
        scalarResult = null;
        sqlText = null;
        status = null;
        error = null;
        attachmentLinks = [];
        attachmentNotes.Clear();
    }

    /// <summary>Opens a query in a blank tab, or a new one when the active tab has been typed in.</summary>
    async Task InsertQuery(string query)
    {
        await StashActiveTab();
        if (tabs.Active.Query.Trim().Length == 0)
        {
            tabs.Active.Query = query;
        }
        else
        {
            tabs.Add(query);
        }

        await LoadActiveTab();
    }

    // ---- Panes ----

    void TogglePlugin(PluginKind kind)
    {
        visiblePlugin = visiblePlugin == kind ? null : kind;
        SchedulePersist();
    }

    void ResetPane(PaneState pane)
    {
        pane.Reset();
        SchedulePersist();
    }

    // Raised from JS on every animation frame of a drag. The size is the container's extent on the
    // dragged axis, which is what lets a threshold be in pixels rather than in a share of a container
    // whose own width is what the drag is changing.
    void OnPaneResize(string resizerId, double fraction, double size)
    {
        const double collapseThreshold = 100;

        switch (resizerId)
        {
            case "plugin-resizer":
                // Dragged almost shut, the pane closes rather than becoming a sliver too narrow to
                // read and too narrow to grab.
                if (fraction * size < collapseThreshold)
                {
                    visiblePlugin = null;
                }
                else
                {
                    pluginPane.Drag(fraction);
                }

                break;
            case "session-resizer":
                sessionPane.Drag(fraction);
                break;
            case "wire-resizer":
                if ((1 - fraction) * size < collapseThreshold)
                {
                    wireExpanded = false;
                }
                else
                {
                    wireExpanded = true;
                    wirePane.Drag(fraction);
                }

                break;
        }

        SchedulePersist();
        StateHasChanged();
    }

    // ---- Shortcuts ----

    // The commands that live outside the editor. The ones inside it (run, copy, SQL) are Monaco
    // actions instead, so they appear in its context menu and follow its focus.
    static readonly Shortcut[] shortcuts =
    [
        new("schema", "s", Ctrl: true, Shift: false, Alt: true, Meta: false),
        new("schema-search", "k", Ctrl: true, Shift: false, Alt: true, Meta: false),
        new("history", "h", Ctrl: true, Shift: false, Alt: true, Meta: false),
        new("settings", ",", Ctrl: true, Shift: false, Alt: false, Meta: false)
    ];

    async void OnGlobalShortcut(string id)
    {
        switch (id)
        {
            case "schema":
                TogglePlugin(PluginKind.Schema);
                break;
            case "schema-search":
                visiblePlugin = PluginKind.Schema;
                StateHasChanged();
                // After the render that opened the pane, so there is an input to focus.
                await Task.Yield();
                await JS.InvokeVoidAsync("scry.focusElement", "#schema-search");
                return;
            case "history":
                TogglePlugin(PluginKind.History);
                break;
            case "settings":
                settingsOpen = !settingsOpen;
                break;
        }

        StateHasChanged();
    }

    // ---- Dialogs ----

    async Task SelectTheme(string mode)
    {
        themeMode = mode;
        await ApplyTheme();
    }

    // Forgets the stored data and the state it came from, together. Removing the keys alone left the
    // tabs, the pane sizes, the plugin, and the theme in memory as they were, and the next save — any
    // keystroke, tab switch, or pane drag — wrote every one of them straight back.
    async Task ClearStorage()
    {
        storage.Clear();
        storage.RawRemove(ThemeKey);
        storage.RawRemove(HistoryStore.LegacyKey);
        history.Load(null);

        tabs.Reset(Sample);
        pluginPane.Reset();
        sessionPane.Reset();
        wirePane.Reset();
        visiblePlugin = PluginKind.Schema;
        wireExpanded = true;
        themeMode = "system";
        await Retint();

        // Through the editor, as a tab switch goes: it holds the text, and the sample it is given
        // now is what the shell restores to on the next visit.
        await LoadActiveTab();
    }

    // ---- Schema ----

    /// <summary>
    /// Re-reads the schema, for when the server was rebuilt under an open explorer. The providers
    /// read the workspace field on every call, so swapping it in is enough for completion and hover;
    /// the executor is dropped because it holds a compilation built against the old one.
    /// </summary>
    async Task RefetchSchema()
    {
        refetching = true;
        ready = false;
        StateHasChanged();
        try
        {
            var json = await Http.GetStringAsync("introspect");
            introspection = ScryJson.DeserializeIntrospection(json);
            workspace = RoslynWorkspace.Create(ModelSynthesizer.Synthesize(introspection), scryReferences!);
            executor = null;
            error = null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            refetching = false;
            ready = workspace is not null && registered;
            StateHasChanged();
        }
    }

    // ---- Persistence ----

    void SchedulePersist() =>
        persist?.Run(() =>
        {
            Persist();
            return Task.CompletedTask;
        });

    void Persist()
    {
        // Another window of this explorer may have written since this one last read. Its tabs are
        // adopted before this one writes, so neither window's save loses the other's tabs — and an
        // adopted tab is on the bar, so the bar is redrawn.
        if (tabs.Merge(storage.Get(TabsKey)))
        {
            _ = InvokeAsync(StateHasChanged);
        }

        storage.Set(TabsKey, tabs.Serialize());
        storage.Set(PluginKey, visiblePlugin?.ToString() ?? "");
        storage.Set(PluginFlexKey, pluginPane.Serialize());
        storage.Set(SessionFlexKey, sessionPane.Serialize());
        storage.Set(WireFlexKey, wireExpanded ? wirePane.Serialize() : "collapsed");
    }

    void RestoreShell()
    {
        tabs.Load(storage.Get(TabsKey));
        pluginPane.Load(storage.Get(PluginFlexKey));
        sessionPane.Load(storage.Get(SessionFlexKey));

        var wire = storage.Get(WireFlexKey);
        wireExpanded = wire != "collapsed";
        if (wireExpanded)
        {
            wirePane.Load(wire);
        }

        // An absent value keeps the default; an empty one is a pane the user closed.
        var plugin = storage.Get(PluginKey);
        visiblePlugin = plugin switch
        {
            null => PluginKind.Schema,
            "" => null,
            _ => Enum.TryParse<PluginKind>(plugin, out var kind) ? kind : PluginKind.Schema
        };
    }
}
