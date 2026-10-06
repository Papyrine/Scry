namespace Scry;

public partial class EventView
{
    /// <summary>The fragment the page is at: this view, and the id of the answer it shows.</summary>
    [Parameter]
    public string Address { get; set; } = "";

    [Inject]
    DisclosureClient Client { get; set; } = null!;

    [Inject]
    Session Session { get; set; } = null!;

    [Inject]
    IJSRuntime Js { get; set; } = null!;

    string? shown;
    EventAnswer? answer;
    string? failure;
    string? note;
    bool loading;
    bool settled;

    // As in RowView: an answer to a question the page has since left is dropped.
    int turn;

    protected override async Task OnParametersSetAsync()
    {
        if (Address == shown)
        {
            return;
        }

        shown = Address;
        answer = null;
        failure = null;
        note = null;
        settled = false;
        var asked = DisclosureLink.Parse(Address)["id"];
        if (asked is null)
        {
            settled = true;
            return;
        }

        if (!Guid.TryParse(asked, out var id))
        {
            failure = "That is not the id of an answer.";
            settled = true;
            return;
        }

        var mine = ++turn;
        loading = true;
        var reply = await Client.Event(id);
        if (mine != turn)
        {
            return;
        }

        loading = false;
        settled = true;
        answer = reply.Value;
        failure = reply.Refusal;
    }

    Task ExportCsv() =>
        Export("csv");

    Task ExportJson() =>
        Export("json");

    async Task Export(string format)
    {
        note = null;
        var reply = await Client.Export(new(format, Event: answer!.Event.Id));
        if (reply.Value is not { } file)
        {
            note = reply.Refusal;
            return;
        }

        await Js.InvokeVoidAsync("disclosure.download", file.Name, file.Type, file.Bytes);
    }

    bool Loading => loading;

    // "true" once the answer is in or refused, for a test to wait on; absent until then.
    string? Ready
    {
        get
        {
            if (settled)
            {
                return "true";
            }

            return null;
        }
    }

    bool CanExport => Session.Catalog is {Export: true};

    bool HasFields => answer!.Fields.Count > 0;

    bool HasUnits => answer!.Units.Count > 0;

    string Reader => Session.Catalog?.Reviewer ?? "Whoever is reading";

    string Caller => Show.Caller(answer!.Event.Caller);

    string When => Show.When(answer!.Event.At);

    string Id => answer!.Event.Id.ToString("D");

    string Node => answer!.Event.Node ?? "";

    string Kind
    {
        get
        {
            var line = answer!.Event;
            var kind = line.Kind;
            if (line.Source.Length > 0)
            {
                kind = $"{kind} of {line.Source}";
            }

            if (line.Subscribed)
            {
                kind = $"{kind}, one answer of a live query";
            }

            if (line.Delivery == "Confirmed")
            {
                kind = $"{kind}, confirmed as what the caller already held";
            }

            if (line.ContentType is { } type)
            {
                kind = $"{kind}, as {type}";
            }

            return kind;
        }
    }

    // How it ended and how much of it left. An answer never closed belongs to a process that stopped
    // before it could say, and is read as one that may have been sent.
    string Ended
    {
        get
        {
            var line = answer!.Event;
            if (line.Outcome is not { } outcome)
            {
                return "Never closed: the server stopped before it could say, so this may have been sent";
            }

            var sent = Sent(line.Units ?? 0);

            if (answer.ClosedAt is { } closed)
            {
                return $"{outcome}, with {sent} sent, at {Show.When(closed)}";
            }

            return $"{outcome}, with {sent} sent";
        }
    }

    static string Sent(int units)
    {
        if (units == 0)
        {
            return "nothing";
        }

        if (units == 1)
        {
            return "1 unit";
        }

        return $"{units.ToString(CultureInfo.InvariantCulture)} units";
    }

    string Payload => Show.Json(answer!.Payload);

    string Request => Show.Json(answer!.Request);

    static string Named(MemberLine field) =>
        $"{field.Source}.{field.Member}";

    static string Did(MemberLine field) =>
        field.Use switch
        {
            "Returned" => "Returned: its value left in a row",
            "Aggregated" => "Aggregated: a value folded from it left",
            "Read" => "Read: it decided which rows left, or their order",
            "Traversed" => "Traversed: stepped through to reach another row",
            "Capability" => "Capability: computed for the row and returned",
            _ => field.Use
        };

    static string UnitClass(UnitLine unit)
    {
        if (!unit.Released)
        {
            return "unsent";
        }

        return "";
    }

    // What the unit carried, or what became of it. One the store accepted and the caller was never
    // handed says so first: it is here because it was recorded, not because it was sent.
    static string Carried(UnitLine unit)
    {
        var carried = unit.Text ?? Gone(unit);
        if (!unit.Released)
        {
            return $"(accepted, never sent) {carried}";
        }

        return carried;
    }

    static string Gone(UnitLine unit) =>
        unit.State switch
        {
            "erased" => $"(erased, was {Show.Bytes(unit.Length)})",
            "digest" => $"(not kept: {Show.Bytes(unit.Length)}, known by its address alone)",
            _ => $"(binary, {Show.Bytes(unit.Length)})"
        };

    static string Version(UnitLine unit) =>
        Show.Short(unit.Address);

    static string Shown(EntityLine row) =>
        $"{row.Source}{row.Key}";

    static string Reached(EntityLine row)
    {
        if (row.Via.Length == 0)
        {
            return "The row asked for. Follow to see who else received it.";
        }

        return $"Reached through {row.Via}. Follow to see who else received it.";
    }

    string RowHref(EntityLine row) =>
        Session.Href(DisclosureLink.To(DisclosureLink.Row, ("source", row.Source), ("key", row.Key)));
}
