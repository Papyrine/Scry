using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class ShortKeysDialog
{
    /// <summary>Whether this server offers the SQL preview, which decides if its shortcut is listed.</summary>
    [Parameter]
    public bool SqlPreview { get; set; }

    [Parameter]
    public EventCallback OnClose { get; set; }
}
