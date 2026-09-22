using Microsoft.AspNetCore.Components;

namespace Scry;

/// <summary>
/// Links the stylesheet <see cref="ScryPendingWork"/> is drawn with, which that component renders for
/// itself. Rendered on its own by a page that wants the styles present before the panel first appears.
/// </summary>
/// <remarks>
/// A plain link rather than scoped CSS, so the styles reach the page on a host that never links
/// <c>{App}.styles.css</c>; every class is <c>scry-commands</c>-prefixed to stay out of the app's way.
/// </remarks>
public partial class ScryCommandStyles
{
    /// <summary>The package's own stylesheet, as a static web asset.</summary>
    public const string DefaultHref = "_content/Scry.Client/scry-commands.css";

    /// <summary>
    /// The stylesheet's address: the package's own unless set, and null to link none — for an app that
    /// styles the panel itself.
    /// </summary>
    [Parameter]
    public string? Href { get; set; } = DefaultHref;
}
