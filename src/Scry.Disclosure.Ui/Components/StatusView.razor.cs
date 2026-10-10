namespace Scry;

public partial class StatusView
{
    /// <summary>The fragment the page is at.</summary>
    [Parameter]
    public string Address { get; set; } = "";

    [Inject]
    DisclosureClient Client { get; set; } = null!;

    [Inject]
    Session Session { get; set; } = null!;

    string? shown;
    StatusAnswer? status;
    string? failure;
    bool settled;

    // A check run from here, which is newer than whatever the status last said of one.
    CheckLine? check;
    string? verifyFailure;
    bool verifying;

    protected override async Task OnParametersSetAsync()
    {
        if (Address == shown)
        {
            return;
        }

        shown = Address;
        await Load();
    }

    async Task Load()
    {
        var reply = await Client.Status();
        status = reply.Value;
        failure = reply.Refusal;
        settled = true;
    }

    async Task Verify()
    {
        verifying = true;
        verifyFailure = null;
        var reply = await Client.Verify(new());
        verifying = false;
        if (reply.Value is not { } result)
        {
            verifyFailure = reply.Refusal;
            return;
        }

        check = result;
    }

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

    bool CanVerify => Session.Catalog is {Verify: true};

    bool NoErasures => status!.Erasures.Count == 0;

    bool NoReviews => status!.Reviews.Count == 0;

    string Events => (status!.Events ?? 0).ToString("N0", CultureInfo.InvariantCulture);

    string Pending
    {
        get
        {
            var waiting = status!.Pending ?? 0;
            if (waiting == 0)
            {
                return "Nothing";
            }

            return $"{waiting.ToString("N0", CultureInfo.InvariantCulture)} ({Show.Bytes(status.PendingBytes ?? 0)})";
        }
    }

    string Oldest
    {
        get
        {
            if (status!.OldestPending is { } oldest)
            {
                return Show.When(oldest);
            }

            return "—";
        }
    }

    string Head => Show.Short(status!.ChainHead!);

    CheckLine? Latest => check ?? status!.LastVerification;

    string Checked
    {
        get
        {
            if (Latest is not { } latest)
            {
                return "The chain has not been checked since this server started.";
            }

            var records = latest.Records.ToString("N0", CultureInfo.InvariantCulture);
            if (latest.Intact)
            {
                return $"Checked on {Show.When(latest.At)} UTC: {records} links, each following from the one before.";
            }

            return $"Checked on {Show.When(latest.At)} UTC: the record no longer matches the chain at link {latest.BrokenAt?.ToString(CultureInfo.InvariantCulture)}.";
        }
    }

    string CheckClass
    {
        get
        {
            if (Latest is null)
            {
                return "muted";
            }

            if (Latest.Intact)
            {
                return "verdict yes";
            }

            return "verdict no";
        }
    }

    static string When(ErasureLine erasure) =>
        Show.When(erasure.At);

    static string Row(ErasureLine erasure) =>
        $"{erasure.Source}{erasure.Key}";

    static string By(ErasureLine erasure) =>
        Show.Caller(erasure.By);

    static string Removed(ErasureLine erasure)
    {
        if (erasure.Units == 1)
        {
            return "1 piece of content";
        }

        return $"{erasure.Units.ToString(CultureInfo.InvariantCulture)} pieces of content";
    }

    static string When(ReviewLine review) =>
        Show.When(review.At);

    static string Who(ReviewLine review) =>
        Show.Caller(review.Reviewer);

    static string Asked(ReviewLine review) =>
        review.Question switch
        {
            "Row" => "Who received a row",
            "Caller" => "What a caller received",
            "Member" => "Whether a caller received a member",
            "Event" => "Opened an answer",
            "Export" => "Exported a result",
            "Status" => "How the record stands",
            "Verify" => "Checked the chain",
            "Erase" => "Erased a row",
            _ => review.Question
        };

    // What the answer held, and where it showed content, how many answers' worth.
    static string Answered(ReviewLine review)
    {
        var results = Lines(review.Results);
        if (review.Events == 0)
        {
            return results;
        }

        if (review.Events == 1)
        {
            return $"{results}, and the content of 1 answer";
        }

        return $"{results}, and the content of {review.Events.ToString(CultureInfo.InvariantCulture)} answers";
    }

    static string Lines(int count)
    {
        if (count == 0)
        {
            return "nothing";
        }

        if (count == 1)
        {
            return "1 line";
        }

        return $"{count.ToString(CultureInfo.InvariantCulture)} lines";
    }
}
