// Verify.Playwright captures of the disclosure explorer, which are also the images
// docs/disclosure-audit.md embeds: a reader of that page is looking at these baselines.
//
// One test, and one story told in order, because the explorer shows a record and a record is
// whatever has happened so far: who asked, who was answered, who has since been reading. Captures
// taken by tests running side by side over one record would each depend on how far the others had
// got. Told once, in one order, against a server whose clock is pinned (see DisclosureHost), every
// time on the page and every line of every table is the same on every run.
//
// What cannot be the same is masked: the id of an answer and the head of the chain are made from
// random bits, and the schema stamp moves whenever the sample model gains a member. The conventions
// of UiScreenshotTests otherwise hold — a fixed viewport, grayscale text, and a comparison that
// forgives a pixel's worth of difference between one machine's rendering and another's.
[Category("Browser")]
public class DisclosureScreenshotTests :
    DisclosureFixture
{
    // Wide enough that a row's members sit on one line, which is what the table is for; no taller
    // than what the longest of the captures has to show.
    static ViewportSize viewport = new()
    {
        Width = 1280,
        Height = 760
    };

    [Test]
    public async Task TheExplorer()
    {
        // Two people ask, one of them for the member the model marks sensitive.
        var page = await SignedIn("alice", viewport);
        await Ask(page, Names);
        await Ask(page, Passwords);
        await Ask(page, Count);
        await Become(page, "bob");
        await Ask(page, Names);

        // The auditor asks who was sent Alice's row, opens the answer that carried her password,
        // and goes back — to a list that now has the auditor in it.
        await Become(page, Reviewer);
        await Explore(page, "row?source=Employee&key=1");
        await Answered(page, "row-results");
        await page.Locator("[data-testid='row-table'] tbody tr").Filter(new() {HasText = "Employee.Password"}).Locator("[data-testid='open-event']").ClickAsync();
        await Answered(page, "event-view");
        var failures = new List<Exception>();
        await Capture(page, "Event", failures, "[data-testid='event-id']", "[data-testid='event-stamp']");

        await Host.Settled();
        await page.GoBackAsync();
        await Answered(page, "row-results");
        await Assertions.Expect(page.Locator("[data-testid='row-table'] tbody tr.reviewed")).ToHaveCountAsync(1);
        await Capture(page, "Row", failures);

        // Whether Alice was ever sent a password: a yes, and the answer that makes it one.
        await Explore(page, "member?caller=alice&member=Password&source=Employee");
        await Answered(page, "events");
        await Capture(page, "Member", failures);

        // How the record stands, with its chain checked.
        await Host.Settled();
        await page.Locator("[data-testid='rail-status']").ClickAsync();
        await Answered(page, "status-view");

        // Asking how the record stands was itself recorded, and is a link of the chain once it has
        // been moved on: the check counts the same links every time only if it waits for that.
        await Host.Settled();
        await page.Locator("[data-testid='verify']").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid='status-check']")).ToContainTextAsync(
            "each following from the one before",
            new()
            {
                Timeout = 30_000
            });
        await Capture(page, "Status", failures, "[data-testid='status-head']");

        // Every capture is taken before any is reported, so that one run says everything that moved.
        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    // One capture of the page as it stands, under a name of its own, with whatever differs from run
    // to run painted over.
    static async Task Capture(IPage page, string name, List<Exception> failures, params string[] masked)
    {
        // The pointer is wherever the last click left it, and a hovered row is a different picture.
        await page.Mouse.MoveAsync(0, 0);
        try
        {
            await Verify(page)
                .UseMethodName(name)
                .PageScreenshotOptions(
                    new()
                    {
                        Mask = [.. masked.Select(_ => page.Locator(_))],
                        MaskColor = "#8a8f98"
                    },
                    screenshotOnly: true);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
