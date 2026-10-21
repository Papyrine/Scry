/// <summary>
/// The real Sample.DisclosureServer and a headless Chromium to drive it: a server with the disclosure
/// audit on, a demo sign-in so that its callers and its reviewer have names, and the explorer over
/// its record.
/// </summary>
public sealed class DisclosureHost :
    BrowserHost
{
    string database = "";

    protected override string Project => "Sample.DisclosureServer";

    protected override void Configure(IDictionary<string, string?> environment)
    {
        // The record is timed by a clock the server is handed, so that a screenshot of it shows the
        // same times on every run and on every machine.
        environment["SAMPLE_CLOCK"] = "2026-03-01T09:00:00Z";
        database = environment["SAMPLE_DATABASE"]!;
    }

    /// <summary>
    /// Waits until the record has caught up with what the server accepted.
    /// </summary>
    /// <remarks>
    /// An answer is in the record's outbox before it is sent, and is moved into the tables the
    /// explorer reads a moment later. A test that reads straight after it wrote would sometimes be
    /// reading the moment before. Asked of the database rather than of the explorer, whose own
    /// account of how the record stands is a question, and so would itself be recorded.
    /// </remarks>
    public async Task Settled()
    {
        // The instance Sample.DisclosureServer builds its database in — named here as the server
        // names it, by asking the same thing the same question — and the name this launch gave it.
        var instance = EfLocalDb.Storage.FromSuffix<Sample.Model.SampleContext>("Disclosure").Name;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = $@"(LocalDb)\{instance}",
            InitialCatalog = database,
            IntegratedSecurity = true
        };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM [scry].[DisclosureOutbox] WHERE [Unreadable] = 0";
                if ((int) (await command.ExecuteScalarAsync())! == 0)
                {
                    return;
                }
            }
            catch (Microsoft.Data.SqlClient.SqlException) when (DateTime.UtcNow < deadline)
            {
                // The tables are made by the server's first use of them, which may not have been yet.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The disclosure record did not catch up with what the server accepted.");
            }

            await Task.Delay(25);
        }
    }
}

/// <summary>
/// For fixtures that drive the disclosure explorer: answers are asked for as one demo caller or
/// another, and the record of them is then read as the one name the sample lets read it.
/// </summary>
public abstract class DisclosureFixture :
    BrowserTests
{
    [ClassDataSource<DisclosureHost>(Shared = SharedType.PerClass)]
    public required DisclosureHost Host { get; init; }

    protected override BrowserHost Server => Host;

    /// <summary>The one name the sample lets read the record.</summary>
    protected const string Reviewer = "auditor";

    // The wire requests the tests send, as an agent or any other client would: the JSON AST, posted.

    /// <summary>Every employee's name, in name order.</summary>
    protected const string Names =
        """{"version":1,"root":"Employee","pipeline":[{"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},{"$type":"select","projection":{"members":["Name"]}}]}""";

    /// <summary>The engineers, with the member the model marks sensitive and their department's name.</summary>
    protected const string Passwords =
        """{"version":1,"root":"Employee","pipeline":[{"$type":"where","predicate":{"$type":"binary","op":"Equal","left":{"$type":"member","path":["Department","Name"]},"right":{"$type":"const","value":"Engineering","tag":"String"}}},{"$type":"orderBy","key":{"$type":"member","path":"Name"},"descending":false},{"$type":"select","projection":{"members":["Name","Password",{"name":"Department","value":{"$type":"node","node":{"$type":"member","path":["Department","Name"]}}}]}}]}""";

    /// <summary>How many orders there are: an answer with no row of anybody's in it.</summary>
    protected const string Count =
        """{"version":1,"root":"Order","pipeline":[{"$type":"count"}]}""";

    /// <summary>A page whose visitor is <paramref name="name"/>.</summary>
    protected async Task<IPage> SignedIn(string name, ViewportSize? viewport = null)
    {
        // A locale of the test's own rather than the machine's: a date field shows its empty format,
        // and that is the locale's to choose.
        var options = new BrowserNewPageOptions
        {
            Locale = "en-US"
        };
        if (viewport is not null)
        {
            options.ViewportSize = viewport;
        }

        var page = await NewPageAsync(options);
        await Become(page, name);
        return page;
    }

    /// <summary>Makes the page's visitor <paramref name="name"/>: the demo's stand-in for signing in.</summary>
    protected async Task Become(IPage page, string name) =>
        await page.GotoAsync($"{BaseUrl}/demo/sign-in?as={name}");

    /// <summary>Posts a query as the page's visitor and answers the status it was met with.</summary>
    protected static Task<int> Ask(IPage page, string request) =>
        page.EvaluateAsync<int>(
            "async body => (await fetch('/api/query', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body })).status",
            request);

    /// <summary>
    /// Opens the explorer at a question, by its address. The page boots once; after that a new
    /// address is a change of fragment, which the page follows without loading again.
    /// </summary>
    protected async Task Explore(IPage page, string fragment)
    {
        // What was asked a moment ago is in the record by the time it is asked about.
        await Host.Settled();
        await page.GotoAsync($"{BaseUrl}/scry-disclosures/#{fragment}");
        await page.WaitForSelectorAsync("[data-testid='reviewer']:not(:empty)", 90);
    }

    /// <summary>Waits for a view's answer to be on screen.</summary>
    protected static Task Answered(IPage page, string view) =>
        page.WaitForSelectorAsync($"[data-testid='{view}'][data-ready='true']", 60);
}
