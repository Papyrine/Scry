namespace Scry;

public partial class RowView
{
    /// <summary>The fragment the page is at: this view, and what it was asked.</summary>
    [Parameter]
    public string Address { get; set; } = "";

    [Inject]
    DisclosureClient Client { get; set; } = null!;

    [Inject]
    Session Session { get; set; } = null!;

    [Inject]
    IJSRuntime Js { get; set; } = null!;

    const string KeyHint = "5   or   \"A\", 7";

    // The form.
    string source = "";
    string key = "";
    string from = "";
    string to = "";

    // What is on screen, and the address it is the answer to. Null until the first one is read, so
    // that the first address — which may be empty — is still a change.
    string? shown;
    DisclosureLink link = DisclosureLink.Home;
    List<RowLine> rows = [];
    string? readAs;
    PageMark? next;
    string? failure;
    string? note;
    bool loading;
    bool answered;

    // Counts the questions asked, so that an answer arriving after the page has moved on to another
    // question is dropped rather than shown as that one's.
    int turn;

    // Erasing.
    string confirm = "";
    string? erased;
    string? eraseFailure;
    bool erasing;

    protected override async Task OnParametersSetAsync()
    {
        if (Address == shown)
        {
            return;
        }

        shown = Address;
        link = DisclosureLink.Parse(Address);
        source = link["source"] ?? Default();
        key = link["key"] ?? "";
        from = Show.Typed(link["from"]);
        to = Show.Typed(link["to"]);
        Clear();
        if (Asked)
        {
            await Load(after: null);
        }
    }

    void Clear()
    {
        rows = [];
        readAs = null;
        next = null;
        failure = null;
        note = null;
        answered = false;
        confirm = "";
        erased = null;
        eraseFailure = null;
    }

    // The first source that has a key to ask by, which is the first one a row can be asked about.
    string Default() =>
        Session.Sources.FirstOrDefault(_ => _.Keyed)?.Name ?? "";

    async Task Load(PageMark? after)
    {
        var mine = ++turn;
        loading = true;
        var reply = await Client.Rows(Question(after));
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

        readAs = answer.Key;
        rows.AddRange(answer.Rows);
        next = answer.Next;
    }

    RowQuestion Question(PageMark? after) =>
        new(
            link["source"]!,
            link["key"]!,
            Show.Instant(link["from"]),
            Show.Instant(link["to"]),
            after);

    // Asking is going to the question's address, which is what makes it one that can be linked to.
    // Asked again where the page already is, there is nowhere to go, so it is asked from here.
    async Task Ask()
    {
        var asked = DisclosureLink.To(
            DisclosureLink.Row,
            ("source", source),
            ("key", key.Trim()),
            ("from", Show.Stamp(from)),
            ("to", Show.Stamp(to)));
        if (asked.ToString() != Address)
        {
            Session.Go(asked);
            return;
        }

        Clear();
        if (Asked)
        {
            await Load(after: null);
        }
    }

    Task More() =>
        Load(next);

    Task ExportCsv() =>
        Export("csv");

    Task ExportJson() =>
        Export("json");

    async Task Export(string format)
    {
        note = null;
        var reply = await Client.Export(new(format, Row: Question(after: null)));
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

    async Task Erase()
    {
        erasing = true;
        erased = null;
        eraseFailure = null;
        var reply = await Client.Erase(new(link["source"]!, link["key"]!, confirm));
        erasing = false;
        if (reply.Value is not { } erasure)
        {
            eraseFailure = reply.Refusal;
            return;
        }

        confirm = "";
        erased = $"Erased {Pieces(erasure.Units)} of content. That the row was sent, to whom and when, is still in the record.";
    }

    static string Pieces(int count)
    {
        if (count == 1)
        {
            return "1 piece";
        }

        return $"{count.ToString(CultureInfo.InvariantCulture)} pieces";
    }

    bool NoSources => Session.Sources.Count == 0;

    bool Asked => link["source"] is not null && link["key"] is not null;

    bool Unaskable =>
        source.Length == 0 ||
        string.IsNullOrWhiteSpace(key) ||
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

    bool Any => rows.Count > 0;

    bool Nothing => answered && rows.Count == 0;

    bool HasMore => next is not null;

    bool CanExport => answered && failure is null && rows.Count > 0 && Session.Catalog is {Export: true};

    bool CanErase => answered && failure is null && Session.Catalog is {Erase: true};

    bool CannotErase => erasing || string.IsNullOrWhiteSpace(confirm);

    // The key as the record holds it, once the server has said what it read the typed one as.
    string Heading => $"{link["source"]}{readAs ?? link["key"]}";

    static bool Unkeyed(CatalogSource option) =>
        !option.Keyed;

    // The record says whether any row of a source was ever recorded by key, and not why not: a
    // source with no key and one nobody has yet been sent a row of look the same from here, and
    // either way there is no row of it to ask about.
    static string Label(CatalogSource option)
    {
        if (option.Keyed)
        {
            return option.Name;
        }

        return $"{option.Name} (no row of it recorded)";
    }

    static string RowClass(RowLine row)
    {
        if (row.Review is not null)
        {
            return "reviewed";
        }

        return "";
    }

    // When the row reached somebody: when the answer was made, or when a reviewer was shown it.
    static string When(RowLine row)
    {
        if (row.Review is { } review)
        {
            return Show.When(review.At);
        }

        return Show.When(row.Event.At);
    }

    static string Receiver(RowLine row)
    {
        if (row.Review is { } review)
        {
            return $"{Show.Caller(review.Reviewer)}, reading the record";
        }

        return Show.Caller(row.Event.Caller);
    }

    static string In(RowLine row)
    {
        if (row.Review is not null)
        {
            return $"the answer sent to {Show.Caller(row.Event.Caller)} on {Show.When(row.Event.At)}";
        }

        if (row.Event.Subscribed)
        {
            return $"{row.Event.Kind} of {row.Event.Source}, live";
        }

        return $"{row.Event.Kind} of {row.Event.Source}";
    }

    static string Version(RowLine row) =>
        Show.Short(row.Content);

    static string Reached(RowLine row)
    {
        if (row.Via.Length == 0)
        {
            return "directly";
        }

        return $"through {row.Via}";
    }

    static string ChipClass(MemberLine member)
    {
        if (member.Sensitive)
        {
            return "chip sensitive";
        }

        return "chip";
    }

    static string ChipText(MemberLine member) =>
        $"{member.Source}.{member.Member}";

    static string ChipTitle(MemberLine member)
    {
        if (member.Sensitive)
        {
            return $"{member.Use}, and marked sensitive";
        }

        return member.Use;
    }

    string EventHref(RowLine row) =>
        Session.Href(DisclosureLink.To(DisclosureLink.Event, ("id", row.Event.Id.ToString("D"))));
}
