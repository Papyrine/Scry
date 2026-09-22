using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class Rail
{
    /// <summary>Which pane is open, or null when the plugin column is closed.</summary>
    [Parameter]
    public PluginKind? Visible { get; set; }

    [Parameter]
    public EventCallback<PluginKind> OnToggle { get; set; }

    [Parameter]
    public bool Refetching { get; set; }

    [Parameter]
    public EventCallback OnRefetch { get; set; }

    /// <summary>"system", "light" or "dark".</summary>
    [Parameter]
    public string Theme { get; set; } = "system";

    [Parameter]
    public EventCallback OnThemeToggle { get; set; }

    [Parameter]
    public EventCallback OnOpenShortKeys { get; set; }

    [Parameter]
    public EventCallback OnOpenSettings { get; set; }

    string ThemeLabel => Theme switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System"
    };

    bool Light => Theme == "light";

    bool Dark => Theme == "dark";

    // The space before "active" is written whether or not it follows, as the markup always wrote it.
    string ButtonClass(PluginKind pane)
    {
        if (Visible == pane)
        {
            return "rail-button active";
        }

        return "rail-button ";
    }

    // A string rather than a bool: a bool would render the attribute bare, or not at all.
    string Pressed(PluginKind pane)
    {
        if (Visible == pane)
        {
            return "true";
        }

        return "false";
    }

    // As ButtonClass: the space is written whether or not "spin" follows.
    string RefetchClass
    {
        get
        {
            if (Refetching)
            {
                return "rail-button spin";
            }

            return "rail-button ";
        }
    }
}
