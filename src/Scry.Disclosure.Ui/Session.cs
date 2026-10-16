/// <summary>
/// What the page holds from one view to the next: what the record has anything of, and how to get
/// from where the page is to another question.
/// </summary>
/// <remarks>
/// A service every component is handed rather than parameters each is passed, because what it holds
/// is the page's and not any one view's. A view is told one thing by its parent — the address it is
/// showing — and reads the rest from here.
/// </remarks>
sealed class Session(NavigationManager navigation, IJSRuntime js)
{
    // In-process: the page runs in the browser, and where it is has to be known before its first
    // render rather than a moment after.
    IJSInProcessRuntime browser = (IJSInProcessRuntime) js;

    /// <summary>What the record holds anything of, once the page has asked. Null until then.</summary>
    public CatalogAnswer? Catalog { get; set; }

    /// <summary>The sources there are to ask about, or none before the catalog is read.</summary>
    public IReadOnlyList<CatalogSource> Sources => Catalog?.Sources ?? [];

    /// <summary>
    /// Where a link takes the page: this page's own address with the link as its fragment. The whole
    /// address rather than the fragment alone, because the page has a base and a bare fragment would
    /// resolve against that.
    /// </summary>
    public string Href(DisclosureLink link) =>
        $"{navigation.BaseUri}#{link}";

    /// <summary>Asks a question by going to its address, which is what makes every question one that can be linked to.</summary>
    public void Go(DisclosureLink link) =>
        browser.InvokeVoid("disclosure.go", link.ToString());

    /// <summary>The fragment the page is at, without its <c>#</c>.</summary>
    public string Address => browser.Invoke<string>("disclosure.address").TrimStart('#');
}
