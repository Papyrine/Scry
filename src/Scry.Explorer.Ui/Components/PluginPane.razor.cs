using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class PluginPane
{
    [Parameter]
    public PluginKind Kind { get; set; }

    [Parameter]
    public string? Style { get; set; }

    [Parameter]
    public ScryIntrospection? Introspection { get; set; }

    [Parameter]
    [EditorRequired]
    public HistoryStore History { get; set; } = null!;

    [Parameter]
    public EventCallback<string> OnHistorySelect { get; set; }

    [Parameter]
    public EventCallback<string> OnHistoryRemove { get; set; }

    [Parameter]
    public EventCallback OnHistoryClear { get; set; }

    [Parameter]
    public EventCallback<(string Query, string Label)> OnHistoryLabel { get; set; }

    [Parameter]
    public EventCallback<(string Query, bool Favorite)> OnHistoryFavorite { get; set; }

    [Parameter]
    public EventCallback<string> OnInsertQuery { get; set; }

    // Null until the contract has arrived, which is what the pane says it is loading.
    SchemaIndex? index;
    ScryIntrospection? indexed;

    bool ShowsSchema => Kind == PluginKind.Schema;

    string Title
    {
        get
        {
            if (ShowsSchema)
            {
                return "Schema";
            }

            return "History";
        }
    }

    // The index is derived from the contract, so it is rebuilt only when the contract itself changes —
    // a refetch — rather than on every render of the pane.
    protected override void OnParametersSet()
    {
        if (ReferenceEquals(indexed, Introspection))
        {
            return;
        }

        indexed = Introspection;
        index = Introspection is null ? null : new(Introspection);
    }
}
