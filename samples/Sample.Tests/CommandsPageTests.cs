using Bunit;
using Sample.WebClient.Pages;

/// <summary>
/// The real /commands page against the real pipeline and the sample's own handlers. The page sends and
/// never re-reads, so every change these wait for arrives as the live query's next answer — which is
/// what is being pinned, along with each command's outcome in the status line.
/// </summary>
/// <remarks>
/// A server of its own, since these write. Tests that delete work on rows they hired themselves, so
/// no test depends on another having left the seed alone. The client stops waiting after two seconds,
/// far longer than any handler here takes but the slow rename's four, so an answer these expect inline
/// stays inline on a loaded machine, and the pending path is still real.
/// </remarks>
[TestFixture]
public class CommandsPageTests
{
    ScryTestServer server = null!;

    static readonly string[] seeded = ["Aaron", "Alice", "Bob", "Carol"];

    [OneTimeSetUp]
    public async Task StartServer() =>
        server = await ScryTestServer.StartAsync(liveQueries: true, commands: true, slowDelay: TimeSpan.FromSeconds(4));

    [OneTimeTearDown]
    public async Task StopServer() =>
        await server.DisposeAsync();

    [Test]
    public async Task RendersEachRowWithItsButtons()
    {
        await using var context = Context(server);
        var page = await Ready(context);

        var row = Row(page, "Carol")!;
        Assert.Multiple(() =>
        {
            Assert.That(row.QuerySelector(".toggle")!.TextContent, Is.EqualTo("Deactivate"));
            Assert.That(row.QuerySelector(".rename")!.HasAttribute("disabled"), Is.False);
            Assert.That(page.Find("#create-submit").HasAttribute("disabled"), Is.False);
        });
    }

    // The Delete button is the row's CanDeleteEmployee: the policy's condition, decided in the database.
    [Test]
    public async Task OnlyAnInactiveRowCanBeDeleted()
    {
        await using var context = Context(server);
        var page = await Ready(context);

        var deletable = seeded.Where(_ => !Row(page, _)!.QuerySelector(".delete")!.HasAttribute("disabled"));

        Assert.That(deletable, Is.EqualTo(["Bob"]));
    }

    [Test]
    public async Task DeactivatingARowEnablesItsDeleteAsTheNextAnswer()
    {
        await using var context = Context(server);
        var page = await Ready(context);
        var name = await Hire(page, "Deactivated");

        await Row(page, name)!.QuerySelector(".toggle")!.ClickAsync(new());

        await page.WaitForStateAsync(() => Row(page, name)?.QuerySelector(".delete")?.HasAttribute("disabled") == false, patience);
        Assert.That(Row(page, name)!.QuerySelector(".active")!.TextContent, Is.EqualTo("no"));
    }

    [Test]
    public async Task DeletingARowRemovesItAsTheNextAnswer()
    {
        await using var context = Context(server);
        var page = await Ready(context);
        var name = await Hire(page, "Deleted");
        await Row(page, name)!.QuerySelector(".toggle")!.ClickAsync(new());
        await page.WaitForStateAsync(() => Row(page, name)?.QuerySelector(".delete")?.HasAttribute("disabled") == false, patience);

        await Row(page, name)!.QuerySelector(".delete")!.ClickAsync(new());

        await page.WaitForStateAsync(() => Row(page, name) is null, patience);
        Assert.That(Status(page), Is.EqualTo($"Deleted {name}."));
    }

    // Refused by the handler, which says why in words of its own: the one failure the sample shows.
    [Test]
    public async Task DeletingAManagerFailsWithTheHandlersMessage()
    {
        await using var context = Context(server);
        var page = await Ready(context);
        var name = await Hire(page, "Manager");
        var id = int.Parse(Row(page, name)!.GetAttribute("data-id")!, CultureInfo.InvariantCulture);
        await using (var data = server.NewContext())
        {
            data.Employees.Add(
                new()
                {
                    Name = $"{name} report",
                    DepartmentId = 1,
                    ManagerId = id,
                    Active = true
                });
            await data.SaveChangesAsync();
        }

        await Row(page, name)!.QuerySelector(".toggle")!.ClickAsync(new());
        await page.WaitForStateAsync(() => Row(page, name)?.QuerySelector(".delete")?.HasAttribute("disabled") == false, patience);
        await Row(page, name)!.QuerySelector(".delete")!.ClickAsync(new());

        await page.WaitForStateAsync(() => Status(page)?.Contains("manages others") == true, patience);
        Assert.That(Row(page, name), Is.Not.Null);
    }

    // The typed outcome: the new row's id, read off the result the handler answered with.
    [Test]
    public async Task HiringAnswersWithTheNewRowsId()
    {
        await using var context = Context(server);
        var page = await Ready(context);

        var name = await Hire(page, "Hired");

        var id = Row(page, name)!.GetAttribute("data-id");
        Assert.That(Status(page), Is.EqualTo($"Hired {name} as #{id}."));
    }

    // Past the client's wait the rename is the pending-work panel's, which follows it until it lands —
    // and the table shows it landing, as the next answer.
    [Test]
    public async Task ASlowRenameWaitsInThePanelAndThenLands()
    {
        await using var context = Context(server);
        var page = await Ready(context);
        var panel = context.Render<ScryPendingWork>();
        var name = await Hire(page, "Renamed");
        var renamed = $"{name} slowly";
        await page.Find("#rename-to").ChangeAsync(new() {Value = renamed});

        await Row(page, name)!.QuerySelector(".rename")!.ClickAsync(new());

        await page.WaitForStateAsync(() => Status(page)?.Contains("taking a while") == true, patience);
        await panel.WaitForStateAsync(() => PanelStatus(panel) == "pending", patience);
        await page.WaitForStateAsync(() => Row(page, renamed) is not null, patience);
        await panel.WaitForStateAsync(() => PanelStatus(panel) == "done", patience);
    }

    // A caller the create policy refuses is shown a disabled Hire, from the capabilities the page
    // waited for rather than from a refusal.
    [Test]
    public async Task HiringIsDisabledWhereThePolicyRefusesIt()
    {
        await using var refusing = await ScryTestServer.StartAsync(liveQueries: true, commands: true, allowCreate: false, databaseSuffix: "NoCreate");
        await using var context = Context(refusing);
        var page = await Ready(context);

        Assert.That(page.Find("#create-submit").HasAttribute("disabled"), Is.True);
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static int hires;

    // Hires through the page's own form and waits for the row to arrive as the next answer.
    static async Task<string> Hire(IRenderedComponent<Commands> page, string prefix)
    {
        var name = $"{prefix} {Interlocked.Increment(ref hires)}";
        await page.Find("#create-name").ChangeAsync(new() {Value = name});
        await page.Find("#create").SubmitAsync();
        await page.WaitForStateAsync(() => Row(page, name) is not null, patience);
        return name;
    }

    static async Task<IRenderedComponent<Commands>> Ready(BunitContext context)
    {
        var page = context.Render<Commands>();
        await page.WaitForStateAsync(() => page.Find("#employees").GetAttribute("data-ready") == "true", patience);
        return page;
    }

    static AngleSharp.Dom.IElement? Row(IRenderedComponent<Commands> page, string name) =>
        page.FindAll("#employees tbody tr").FirstOrDefault(_ => _.QuerySelector(".name")?.TextContent == name);

    static string? Status(IRenderedComponent<Commands> page) =>
        page.Find("[data-testid=command-status]").TextContent;

    static string? PanelStatus(IRenderedComponent<ScryPendingWork> panel)
    {
        var items = panel.FindAll("[data-testid=pending-work] li");
        if (items.Count == 0)
        {
            return null;
        }

        return items[^1].GetAttribute("data-status");
    }

    static BunitContext Context(ScryTestServer server)
    {
        var context = new BunitContext();
        var client = server.CreateScryClient();
        client.CommandWait = TimeSpan.FromSeconds(2);
        client.PendingWork.CompletedLinger = TimeSpan.FromMinutes(1);
        context.Services.AddSingleton(client);
        context.Services.AddSingleton(client.PendingWork);
        context.Services.AddSingleton<ScryQuery>();
        return context;
    }
}
