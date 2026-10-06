namespace Scry;

public partial class Rail
{
    /// <summary>Which view is showing: <c>row</c>, <c>caller</c>, <c>member</c>, <c>event</c> or <c>status</c>.</summary>
    [Parameter]
    public string View { get; set; } = "";

    /// <summary>"system", "light" or "dark".</summary>
    [Parameter]
    public string Theme { get; set; } = "system";

    [Parameter]
    public EventCallback OnThemeToggle { get; set; }

    [Inject]
    Session Session { get; set; } = null!;

    // A view with nothing asked of it yet: the rail starts a question, it does not repeat one.
    string Href(string view) =>
        Session.Href(DisclosureLink.To(view));

    string LinkClass(string view)
    {
        if (View == view)
        {
            return "rail-button active";
        }

        return "rail-button";
    }

    // A string or nothing: a bool would render the attribute bare.
    string? Current(string view)
    {
        if (View == view)
        {
            return "page";
        }

        return null;
    }

    bool Light => Theme == "light";

    bool Dark => Theme == "dark";

    string ThemeLabel => Theme switch
    {
        "light" => "Switch theme (currently Light)",
        "dark" => "Switch theme (currently Dark)",
        _ => "Switch theme (currently System)"
    };
}
