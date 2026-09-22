using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class SettingsDialog
{
    static string[] modes = ["system", "light", "dark"];

    [Parameter]
    public string Theme { get; set; } = "system";

    [Parameter]
    public EventCallback<string> OnThemeSelected { get; set; }

    [Parameter]
    public EventCallback OnClear { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }

    string clearLabel = "Clear data";

    static string Label(string mode) => mode switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System"
    };

    // The space before "active" is written whether or not it follows, as the markup always wrote it.
    string ChoiceClass(string mode)
    {
        if (Theme == mode)
        {
            return "choice active";
        }

        return "choice ";
    }

    // A string rather than a bool: a bool would render the attribute bare, or not at all.
    string Pressed(string mode)
    {
        if (Theme == mode)
        {
            return "true";
        }

        return "false";
    }

    // The label reports what happened and then goes back, so the button says whether the click landed
    // without a dialog of its own.
    async Task Clear()
    {
        await OnClear.InvokeAsync();
        clearLabel = "Cleared";
        StateHasChanged();
        await Task.Delay(2000);
        clearLabel = "Clear data";
    }
}
