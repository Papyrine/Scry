namespace Scry;

/// <summary>
/// Configures the opt-in disclosure explorer mapped by
/// <see cref="ScryDisclosureExplorerExtensions.MapScryDisclosureExplorer(IEndpointRouteBuilder, Action{ScryDisclosureExplorerOptions})"/>.
/// </summary>
public sealed class ScryDisclosureExplorerOptions
{
    // begin-snippet: disclosureExplorerOptions
    /// <summary>Sub-path the explorer is served under. Default <c>/scry-disclosures</c>.</summary>
    public string Route { get; set; } = "/scry-disclosures";

    /// <summary>
    /// Decides, per request, whether the explorer is reachable at all. Development-only by default:
    /// it is a window onto everything every caller was ever sent, so it stays shut in production
    /// until a host opens it to somebody on purpose.
    /// </summary>
    public Func<HttpContext, bool> EnableGuard { get; set; } = DevelopmentOnly;

    /// <summary>
    /// Decides, per request, whether a result may be written out as a file. Development-only by
    /// default, and separate from <see cref="EnableGuard"/>: a screen shows one event at a time,
    /// and an export is every event of a question leaving at once.
    /// </summary>
    public Func<HttpContext, bool> EnableExport { get; set; } = DevelopmentOnly;

    /// <summary>
    /// Decides, per request, whether a row's content may be erased from the explorer. Off by
    /// default, everywhere: an erasure cannot be taken back, so who may make one is the host's to say.
    /// </summary>
    public Func<HttpContext, bool> EnableErase { get; set; } = _ => false;

    /// <summary>
    /// Who is reading the record: what every question is recorded under. The authenticated name by
    /// default. A request that names nobody is refused, unless the audit allows anonymous callers.
    /// </summary>
    /// <remarks>
    /// Read from the authenticated principal, never from something the browser supplied: this is the
    /// name the record of who read the record is kept under.
    /// </remarks>
    public Func<HttpContext, string?> Reviewer { get; set; } = _ => _.User.Identity?.Name;

    /// <summary>
    /// The most events one export writes. Default 1,000. An export past it holds the newest that
    /// many and says that it was cut short.
    /// </summary>
    public int ExportLimit { get; set; } = 1000;
    // end-snippet

    /// <summary>The default <see cref="EnableGuard"/>: open only in the Development environment.</summary>
    public static bool DevelopmentOnly(HttpContext context) =>
        context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
}
