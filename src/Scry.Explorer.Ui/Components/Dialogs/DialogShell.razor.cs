using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Scry;

public partial class DialogShell
{
    [Parameter]
    [EditorRequired]
    public string Title { get; set; } = "";

    [Parameter]
    public string? TestId { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    ElementReference panel;

    Task OnKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            return OnClose.InvokeAsync();
        }

        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await panel.FocusAsync();
        }
    }
}
