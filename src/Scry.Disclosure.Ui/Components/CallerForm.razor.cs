namespace Scry;

public partial class CallerForm
{
    /// <summary>The fragment the page is at: this view, and what it was asked.</summary>
    [Parameter]
    public string Address { get; set; } = "";

    [Inject]
    Session Session { get; set; } = null!;

    string caller = "";
    bool nobody;
    string from = "";
    string to = "";
    string? shown;

    // The form shows what the page is asking: a link opened cold fills it in.
    protected override void OnParametersSet()
    {
        if (Address == shown)
        {
            return;
        }

        shown = Address;
        var link = DisclosureLink.Parse(Address);
        caller = link["caller"] ?? "";
        nobody = link["nobody"] is not null;
        from = Show.Typed(link["from"]);
        to = Show.Typed(link["to"]);
    }

    bool Unaskable =>
        (!nobody && string.IsNullOrWhiteSpace(caller)) ||
        Show.Mistyped(from) ||
        Show.Mistyped(to);

    string? FromClass => Marked(from);

    string? ToClass => Marked(to);

    // Marked where what was typed is not a time, so that it is the field that says why the question
    // cannot be asked.
    static string? Marked(string typed)
    {
        if (Show.Mistyped(typed))
        {
            return "mistyped";
        }

        return null;
    }

    void Ask() =>
        Session.Go(
            DisclosureLink.To(
                DisclosureLink.Caller,
                ("caller", Named()),
                ("nobody", Anonymous()),
                ("from", Show.Stamp(from)),
                ("to", Show.Stamp(to))));

    string? Named()
    {
        if (nobody)
        {
            return null;
        }

        return caller.Trim();
    }

    string? Anonymous()
    {
        if (nobody)
        {
            return "1";
        }

        return null;
    }
}
