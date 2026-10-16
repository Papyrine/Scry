namespace Scry;

public partial class MemberForm
{
    /// <summary>The fragment the page is at: this view, and what it was asked.</summary>
    [Parameter]
    public string Address { get; set; } = "";

    [Inject]
    Session Session { get; set; } = null!;

    string caller = "";
    bool nobody;
    string source = "";
    string member = "";
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
        source = link["source"] ?? FirstSource();
        member = link["member"] ?? First();
    }

    // The members of the chosen source that some answer read, which are the ones there is anything
    // to ask about.
    IReadOnlyList<CatalogMember> Members =>
        Session.Sources.FirstOrDefault(_ => _.Name == source)?.Members ?? [];

    string FirstSource()
    {
        if (Session.Sources.Count == 0)
        {
            return "";
        }

        return Session.Sources[0].Name;
    }

    string First()
    {
        var members = Members;
        if (members.Count == 0)
        {
            return "";
        }

        return members[0].Name;
    }

    // A member belongs to a source, so choosing another source is choosing among other members.
    void Chose(ChangeEventArgs chosen)
    {
        source = chosen.Value?.ToString() ?? "";
        member = First();
    }

    static string Label(CatalogMember option)
    {
        if (option.Sensitive)
        {
            return $"{option.Name} (sensitive)";
        }

        return option.Name;
    }

    bool Unaskable =>
        source.Length == 0 ||
        member.Length == 0 ||
        (!nobody && string.IsNullOrWhiteSpace(caller));

    void Ask() =>
        Session.Go(
            DisclosureLink.To(
                DisclosureLink.Member,
                ("caller", Named()),
                ("nobody", Anonymous()),
                ("source", source),
                ("member", member)));

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
