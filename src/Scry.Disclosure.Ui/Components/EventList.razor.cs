namespace Scry;

public partial class EventList
{
    /// <summary>The fragment the page is at: the caller view or the member view, and what it was asked.</summary>
    [Parameter]
    public string Address { get; set; } = "";

    [Inject]
    DisclosureClient Client { get; set; } = null!;

    [Inject]
    Session Session { get; set; } = null!;

    [Inject]
    IJSRuntime Js { get; set; } = null!;

    string? shown;
    DisclosureLink link = DisclosureLink.Home;
    List<EventLine> events = [];
    PageMark? next;
    string? failure;
    string? note;
    bool loading;
    bool answered;

    // As in RowView: an answer to a question the page has since left is dropped.
    int turn;

    protected override async Task OnParametersSetAsync()
    {
        if (Address == shown)
        {
            return;
        }

        shown = Address;
        link = DisclosureLink.Parse(Address);
        events = [];
        next = null;
        failure = null;
        note = null;
        answered = false;
        if (Asked)
        {
            await Load(after: null);
        }
    }

    async Task Load(PageMark? after)
    {
        var mine = ++turn;
        loading = true;
        var reply = await Ask(after);
        if (mine != turn)
        {
            return;
        }

        loading = false;
        answered = true;
        if (reply.Value is not { } answer)
        {
            failure = reply.Refusal;
            return;
        }

        events.AddRange(answer.Events);
        next = answer.Next;
    }

    Task<Reply<EventsAnswer>> Ask(PageMark? after)
    {
        if (ForMember)
        {
            return Client.Members(MemberAsked(after));
        }

        return Client.Callers(CallerAsked(after));
    }

    CallerQuestion CallerAsked(PageMark? after) =>
        new(
            link["caller"],
            Show.Instant(link["from"]),
            Show.Instant(link["to"]),
            after);

    MemberQuestion MemberAsked(PageMark? after) =>
        new(link["caller"], link["source"]!, link["member"]!, after);

    Task More() =>
        Load(next);

    Task ExportCsv() =>
        Export("csv");

    Task ExportJson() =>
        Export("json");

    async Task Export(string format)
    {
        note = null;
        var reply = await Client.Export(Exported(format));
        if (reply.Value is not { } file)
        {
            note = reply.Refusal;
            return;
        }

        await Js.InvokeVoidAsync("disclosure.download", file.Name, file.Type, file.Bytes);
        if (file.Cut)
        {
            note = "That file holds only the newest answers: there were more than one export writes.";
        }
    }

    ExportQuestion Exported(string format)
    {
        if (ForMember)
        {
            return new(format, Member: MemberAsked(after: null));
        }

        return new(format, Caller: CallerAsked(after: null));
    }

    bool ForMember => link.View == DisclosureLink.Member;

    // A caller by name, or the answers recorded against nobody; and for the member view, a member.
    bool Asked
    {
        get
        {
            if (link["caller"] is null &&
                link["nobody"] is null)
            {
                return false;
            }

            if (ForMember)
            {
                return link["source"] is not null && link["member"] is not null;
            }

            return true;
        }
    }

    bool Answered => answered;

    // "true" once the answer is in, for a test to wait on; absent until then.
    string? Ready
    {
        get
        {
            if (answered)
            {
                return "true";
            }

            return null;
        }
    }

    bool Any => events.Count > 0;

    bool Nothing => events.Count == 0;

    bool HasMore => next is not null;

    bool CanExport => answered && failure is null && events.Count > 0 && Session.Catalog is {Export: true};

    string Whom => Show.Caller(link["caller"]);

    string Heading
    {
        get
        {
            if (ForMember)
            {
                return $"{Whom} and {link["source"]}.{link["member"]}";
            }

            return $"Sent to {Whom}";
        }
    }

    // The answer to the member question, in a sentence: it is a yes or a no before it is a list.
    string Verdict
    {
        get
        {
            var named = $"{link["source"]}.{link["member"]}";
            if (events.Count == 0)
            {
                return $"No. The record does not say {Whom} was ever sent {named}.";
            }

            return $"Yes. {Whom} was sent {named}, most recently on {Show.When(events[0].At)} UTC.";
        }
    }

    string VerdictClass
    {
        get
        {
            if (events.Count == 0)
            {
                return "verdict no";
            }

            return "verdict yes";
        }
    }

    static string When(EventLine answer) =>
        Show.When(answer.At);

    static string Of(EventLine answer)
    {
        if (answer.Source.Length == 0)
        {
            return "no source";
        }

        return answer.Source;
    }

    // How many units left. Unknown where the answer was never closed: the process stopped before it
    // could say, and what it had accepted may have gone.
    static string Sent(EventLine answer)
    {
        if (answer.Units is not { } units)
        {
            return "unknown";
        }

        if (units == 1)
        {
            return "1 unit";
        }

        return $"{units.ToString(CultureInfo.InvariantCulture)} units";
    }

    static string Ended(EventLine answer) =>
        answer.Outcome ?? "Never closed";

    static IEnumerable<string> Flags(EventLine answer)
    {
        if (answer.Sensitive)
        {
            yield return "sensitive";
        }

        if (answer.Subscribed)
        {
            yield return "live";
        }

        if (answer.Delivery == "Confirmed")
        {
            yield return "confirmed";
        }
    }

    static string FlagClass(string flag)
    {
        if (flag == "sensitive")
        {
            return "chip sensitive";
        }

        return "chip";
    }

    string EventHref(EventLine answer) =>
        Session.Href(DisclosureLink.To(DisclosureLink.Event, ("id", answer.Id.ToString("D"))));
}
