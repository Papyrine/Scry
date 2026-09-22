using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class OutputTabs
{
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<OutputTab> Available { get; set; } = [];

    [Parameter]
    public OutputTab Active { get; set; }

    [Parameter]
    public EventCallback<OutputTab> OnSelect { get; set; }

    static string Label(OutputTab tab) => tab switch
    {
        OutputTab.Result => "Result",
        OutputTab.Response => "Response",
        _ => "SQL"
    };

    static string Slug(OutputTab tab) => tab switch
    {
        OutputTab.Result => "result",
        OutputTab.Response => "response",
        _ => "sql"
    };

    // The space before "active" is written whether or not it follows, as the markup always wrote it.
    string TabClass(OutputTab tab)
    {
        if (Active == tab)
        {
            return "output-tab active";
        }

        return "output-tab ";
    }

    // A string rather than a bool: a bool would render the attribute bare, or not at all.
    string Selected(OutputTab tab)
    {
        if (Active == tab)
        {
            return "true";
        }

        return "false";
    }
}
