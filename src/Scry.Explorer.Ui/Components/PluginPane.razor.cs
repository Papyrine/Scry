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

    /// <summary>The active tab's query, which the query builder reads its rows from.</summary>
    [Parameter]
    public string Query { get; set; } = "";

    /// <summary>Raised with an edit the query builder made, for the app to apply to the editor.</summary>
    [Parameter]
    public EventCallback<Func<string, string?>> OnBuilderEdit { get; set; }

    // Null until the contract has arrived, which is what the pane says it is loading.
    SchemaIndex? index;
    ScryIntrospection? indexed;

    string Title =>
        Kind switch
        {
            PluginKind.Schema => "Schema",
            PluginKind.Builder => "Query builder",
            _ => "History"
        };

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
