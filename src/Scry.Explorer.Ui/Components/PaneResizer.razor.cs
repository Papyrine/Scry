using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class PaneResizer
{
    [Parameter]
    [EditorRequired]
    public string Id { get; set; } = "";

    /// <summary>"x" for a bar between side-by-side panes, "y" for one between stacked panes.</summary>
    [Parameter]
    [EditorRequired]
    public string Direction { get; set; } = "x";

    [Parameter]
    public bool Hidden { get; set; }

    [Parameter]
    public EventCallback OnReset { get; set; }

    // The space before "hidden" is written whether or not it follows, as the markup always wrote it.
    string Class
    {
        get
        {
            if (Hidden)
            {
                return $"resizer resizer-{Direction} hidden";
            }

            return $"resizer resizer-{Direction} ";
        }
    }

    string Orientation
    {
        get
        {
            if (Direction == "x")
            {
                return "vertical";
            }

            return "horizontal";
        }
    }
}
