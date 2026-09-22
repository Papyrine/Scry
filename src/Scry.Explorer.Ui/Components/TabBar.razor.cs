using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Scry;

public partial class TabBar
{
    [Parameter]
    [EditorRequired]
    public TabStore Store { get; set; } = null!;

    [Parameter]
    public EventCallback<int> OnActivate { get; set; }

    [Parameter]
    public EventCallback<int> OnClose { get; set; }

    [Parameter]
    public EventCallback OnAdd { get; set; }

    [Parameter]
    public EventCallback<(int Index, string Title)> OnRename { get; set; }

    ElementReference renameInput;
    int? renaming;
    string renameText = "";
    bool focusPending;

    // The close goes with the second tab: an explorer with no tab has nowhere to type.
    bool Closable => Store.Tabs.Count > 1;

    // Each tab with its position, which is what the tab's handlers are called with.
    IEnumerable<(int Index, TabState Tab)> Indexed()
    {
        for (var index = 0; index < Store.Tabs.Count; index++)
        {
            yield return (index, Store.Tabs[index]);
        }
    }

    bool Renaming(int index) =>
        renaming == index;

    // The space before "active" is written whether or not it follows, as the markup always wrote it.
    string TabClass(int index)
    {
        if (index == Store.ActiveIndex)
        {
            return "tab active";
        }

        return "tab ";
    }

    // A string rather than a bool: a bool would render the attribute bare, or not at all.
    string Selected(int index)
    {
        if (index == Store.ActiveIndex)
        {
            return "true";
        }

        return "false";
    }

    void StartRename(int index)
    {
        renaming = index;
        renameText = Store.Title(Store.Tabs[index]);
        focusPending = true;
    }

    // Escape cancels and Enter commits. Blur cancels too, so clicking away from a rename leaves the
    // title alone rather than committing a half-typed one.
    async Task OnRenameKey(KeyboardEventArgs args, int index)
    {
        if (args.Key == "Enter")
        {
            renaming = null;
            await OnRename.InvokeAsync((index, renameText));
            return;
        }

        if (args.Key == "Escape")
        {
            CancelRename();
        }
    }

    void CancelRename() =>
        renaming = null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusPending)
        {
            return;
        }

        focusPending = false;
        await renameInput.FocusAsync();
    }
}
