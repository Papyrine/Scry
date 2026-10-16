namespace Scry;

public partial class App :
    IDisposable
{
    [Inject]
    DisclosureClient Client { get; set; } = null!;

    [Inject]
    Session Session { get; set; } = null!;

    [Inject]
    IJSRuntime Js { get; set; } = null!;

    // The fragment the page is at, and the same thing read as a view and what it was asked.
    string address = "";
    DisclosureLink link = DisclosureLink.Home;

    string? failure;
    string theme = "system";
    DotNetObjectReference<App>? watching;

    protected override async Task OnInitializedAsync()
    {
        var browser = (IJSInProcessRuntime) Js;
        Read(Session.Address);

        // The browser says when the fragment moves: a link followed, Back pressed, a question asked.
        // The framework's own navigation is not asked, because a move of the fragment alone is one
        // it does not report.
        watching = DotNetObjectReference.Create(this);
        browser.InvokeVoid("disclosure.watch", watching);

        // Synchronously, before the first render: the page already wears this theme, and a toggle
        // that started from the wrong one would take two clicks to seem to do anything.
        theme = browser.Invoke<string>("disclosure.theme");

        var reply = await Client.Catalog();
        Session.Catalog = reply.Value;
        failure = reply.Refusal;
    }

    /// <summary>Called by the browser each time the page's fragment changes.</summary>
    [JSInvokable]
    public void Moved(string fragment)
    {
        Read(fragment.TrimStart('#'));
        StateHasChanged();
    }

    void Read(string fragment)
    {
        address = fragment;
        link = DisclosureLink.Parse(address);
    }

    string View => link.View;

    bool Booting => Session.Catalog is null;

    bool ShowingRow => link.View == DisclosureLink.Row;

    bool ShowingCaller => link.View == DisclosureLink.Caller;

    bool ShowingMember => link.View == DisclosureLink.Member;

    bool ShowingEvent => link.View == DisclosureLink.Event;

    string Title => link.View switch
    {
        DisclosureLink.Row => "Who received a row",
        DisclosureLink.Caller => "What a caller received",
        DisclosureLink.Member => "Whether a caller received a member",
        DisclosureLink.Event => "One answer",
        _ => "The record"
    };

    // Who the server takes the reader to be, which is who every question here is recorded under.
    string Reading
    {
        get
        {
            if (Session.Catalog is null)
            {
                return "";
            }

            if (Session.Catalog.Reviewer is { } reviewer)
            {
                return $"Reading as {reviewer}";
            }

            return "Reading as nobody";
        }
    }

    string ReadingClass
    {
        get
        {
            if (Session.Catalog is {Reviewer: null})
            {
                return "reading unnamed";
            }

            return "reading";
        }
    }

    // System → Light → Dark → System.
    void ToggleTheme()
    {
        theme = theme switch
        {
            "system" => "light",
            "light" => "dark",
            _ => "system"
        };
        ((IJSInProcessRuntime) Js).InvokeVoid("disclosure.setTheme", theme);
    }

    public void Dispose() =>
        watching?.Dispose();
}
