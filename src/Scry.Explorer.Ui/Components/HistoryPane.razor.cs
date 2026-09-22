using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Scry;

public partial class HistoryPane
{
    [Parameter]
    [EditorRequired]
    public HistoryStore Store { get; set; } = null!;

    [Parameter]
    public EventCallback<string> OnSelect { get; set; }

    [Parameter]
    public EventCallback<string> OnRemove { get; set; }

    [Parameter]
    public EventCallback OnClear { get; set; }

    [Parameter]
    public EventCallback<(string Query, string Label)> OnLabel { get; set; }

    [Parameter]
    public EventCallback<(string Query, bool Favorite)> OnFavorite { get; set; }

    ElementReference labelInput;
    string? editing;
    string labelText = "";
    string? filter;
    bool focusPending;

    bool Empty => Store.Count == 0;

    IEnumerable<HistoryItem> Shown =>
        Store.Items.Where(_ => HistoryStore.Matches(_, filter));

    bool Editing(HistoryItem item) =>
        editing == item.Query;

    // The space before "favorite" is written whether or not it follows, as the markup always wrote it.
    static string RowClass(HistoryItem item)
    {
        if (item.Favorite)
        {
            return "history-row favorite";
        }

        return "history-row ";
    }

    // As RowClass: the space is written whether or not "active" follows.
    static string FavoriteClass(HistoryItem item)
    {
        if (item.Favorite)
        {
            return "icon-btn active";
        }

        return "icon-btn ";
    }

    static string FavoriteLabel(HistoryItem item)
    {
        if (item.Favorite)
        {
            return "Remove favorite";
        }

        return "Keep this query";
    }

    static string FavoriteTitle(HistoryItem item)
    {
        if (item.Favorite)
        {
            return "Remove favorite. It goes back under the cap.";
        }

        return "Keep this query. Favorites are never evicted.";
    }

    static string StarFill(HistoryItem item)
    {
        if (item.Favorite)
        {
            return "currentColor";
        }

        return "none";
    }

    Task ToggleFavorite(HistoryItem item) =>
        OnFavorite.InvokeAsync((item.Query, !item.Favorite));

    void StartEdit(HistoryItem item)
    {
        editing = item.Query;
        labelText = item.Label ?? "";
        focusPending = true;
    }

    // Escape cancels. GraphiQL commits on Escape, which loses whatever the label was before the edit
    // started — there is no way back from that, so the cancel is the safer of the two.
    async Task OnLabelKey(KeyboardEventArgs args, string query)
    {
        if (args.Key == "Enter")
        {
            editing = null;
            await OnLabel.InvokeAsync((query, labelText));
            return;
        }

        if (args.Key == "Escape")
        {
            CancelEdit();
        }
    }

    void CancelEdit() =>
        editing = null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!focusPending)
        {
            return;
        }

        focusPending = false;
        await labelInput.FocusAsync();
    }
}
