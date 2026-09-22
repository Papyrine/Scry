using Bunit;

/// <summary>
/// The pending-work panel over a scripted command server: nothing while there is nothing to show, open
/// as a command goes pending, and each command shown to its end — done, failed, or lost track of.
/// </summary>
[TestFixture]
public class PendingWorkComponentTests
{
    static RenameThing Rename => new()
    {
        Id = 5,
        Name = "Renamed"
    };

    [Test]
    public async Task IdleRendersOnlyTheStylesheet()
    {
        await using var context = new BunitContext();
        var client = new CommandStub().Client();

        var component = context.Render<ScryPendingWork>(_ => _.Add(panel => panel.Store, client.PendingWork));

        Assert.Multiple(() =>
        {
            Assert.That(component.Find("link").GetAttribute("href"), Is.EqualTo(ScryCommandStyles.DefaultHref));
            Assert.That(component.FindAll("button, aside"), Is.Empty);
        });
    }

    [Test]
    public async Task ANullStylesheetLinksNone()
    {
        await using var context = new BunitContext();
        var client = new CommandStub().Client();

        var component = context.Render<ScryPendingWork>(
            _ => _
                .Add(panel => panel.Store, client.PendingWork)
                .Add(panel => panel.StylesHref, null));

        Assert.That(component.Markup.Trim(), Is.Empty);
    }

    [Test]
    public async Task OpensWhenACommandGoesPending()
    {
        await using var held = new HeldReceipts();
        await using var context = new BunitContext();
        var (component, client, stub) = Panel(context, held.Step());

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;

        await Rendered(component, () => Status(component) == "pending");

        var item = component.Find("[data-testid=pending-work] li");
        Assert.Multiple(() =>
        {
            Assert.That(item.GetAttribute("data-command"), Is.EqualTo("RenameThing"));
            Assert.That(item.TextContent, Does.Contain("RenameThing · Thing 5"));
            Assert.That(component.Find("[data-testid=pending-work-count]").TextContent, Is.EqualTo("1"));
        });
    }

    [Test]
    public async Task StaysShutWhenNotToOpenByItself()
    {
        await using var held = new HeldReceipts();
        await using var context = new BunitContext();
        var (component, client, stub) = Panel(context, held.Step(), autoOpen: false);

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;

        await Rendered(component, () => component.FindAll("[data-testid=pending-work-toggle]").Count == 1);

        Assert.That(component.FindAll("[data-testid=pending-work]"), Is.Empty);
    }

    // A linger longer than the test, so the done state is still drawn whenever the test looks: with a
    // short one, a test running late can find it already gone.
    [Test]
    public async Task ACompletedCommandIsShownDone()
    {
        await using var held = new HeldReceipts();
        await using var context = new BunitContext();
        var (component, client, stub) = Panel(context, held.Step());
        client.PendingWork.CompletedLinger = TimeSpan.FromMinutes(1);

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;
        await held.Send(CommandStub.Result(CommandStatus.Completed));

        await Rendered(component, () => Status(component) == "done");
    }

    // Unlike a failure, which waits to be read, a completed command leaves the panel by itself.
    [Test]
    public async Task ACompletedCommandLeavesByItself()
    {
        await using var held = new HeldReceipts();
        await using var context = new BunitContext();
        var (component, client, stub) = Panel(context, held.Step());
        client.PendingWork.CompletedLinger = TimeSpan.FromMilliseconds(50);

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;
        await Rendered(component, () => Status(component) == "pending");
        await held.Send(CommandStub.Result(CommandStatus.Completed));

        await Rendered(component, () => component.FindAll("aside, button").Count == 0);
    }

    [Test]
    public async Task AFailedCommandShowsWhy()
    {
        await using var held = new HeldReceipts();
        await using var context = new BunitContext();
        var (component, client, stub) = Panel(context, held.Step());

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;
        await held.Send(CommandStub.Result(CommandStatus.Failed, error: "The manager still has reports."));

        await Rendered(component, () => Status(component) == "failed");

        Assert.That(component.Find("[data-testid=pending-work-error]").TextContent, Is.EqualTo("The manager still has reports."));
    }

    // Cut, asked for again, and not found: the panel says so rather than leaving it pending for ever.
    [Test]
    public async Task ACommandLostTrackOfIsShownUnknown()
    {
        var held = new HeldReceipts();
        await using var context = new BunitContext();
        var (component, client, stub) = Panel(
            context,
            held.Step(),
            CommandStub.Refusal(HttpStatusCode.NotFound, ScryErrorCode.NotFound, "The command is not one this server holds."));

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;
        await held.DisposeAsync();

        await Rendered(component, () => Status(component) == "unknown");

        Assert.That(component.Find("[data-testid=pending-work-error]").TextContent, Does.Contain("may have run"));
    }

    [Test]
    public async Task ClosesToItsLauncherAndClearsWhatFinished()
    {
        await using var held = new HeldReceipts();
        await using var context = new BunitContext();
        var (component, client, stub) = Panel(context, held.Step());

        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;
        await held.Send(CommandStub.Result(CommandStatus.Failed, error: "No."));
        await Rendered(component, () => Status(component) == "failed");

        await component.Find("[data-testid=pending-work-close]").ClickAsync(new());
        Assert.That(component.FindAll("[data-testid=pending-work]"), Is.Empty);

        await component.Find("[data-testid=pending-work-toggle]").ClickAsync(new());
        await component.Find("[data-testid=pending-work-clear]").ClickAsync(new());

        await Rendered(component, () => component.FindAll("aside, button").Count == 0);
    }

    // Registered beside the client, the panel needs nothing passed to it.
    [Test]
    public async Task FindsTheStoreRegisteredBesideTheClient()
    {
        await using var held = new HeldReceipts();
        var stub = new CommandStub(held.Step());
        await using var context = new BunitContext();
        context.Services.AddScoped(_ => new HttpClient(stub.Handler()) {BaseAddress = new("http://localhost")});
        context.Services.AddScryClient("/api/query");
        var client = context.Services.GetRequiredService<ScryClient>();
        client.CommandWait = TimeSpan.Zero;

        var component = context.Render<ScryPendingWork>();
        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        await sending;

        await Rendered(component, () => component.FindAll("[data-testid=pending-work] li").Count == 1);
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    // Waited for by predicate rather than by assertion: NUnit records a failed assertion as the test's
    // failure even where bUnit catches it to try again after the next render.
    static Task Rendered(IRenderedComponent<ScryPendingWork> component, Func<bool> reached) =>
        component.WaitForStateAsync(reached, patience);

    static string? Status(IRenderedComponent<ScryPendingWork> component)
    {
        var items = component.FindAll("[data-testid=pending-work] li");
        if (items.Count == 0)
        {
            return null;
        }

        return items[0].GetAttribute("data-status");
    }

    static (IRenderedComponent<ScryPendingWork> Component, ScryClient Client, CommandStub Stub) Panel(
        BunitContext context,
        Func<Guid, HttpResponseMessage> first,
        Func<Guid, HttpResponseMessage>? second = null,
        bool autoOpen = true)
    {
        var stub = second is null ? new CommandStub(first) : new CommandStub(first, second);
        var client = stub.Client(commandWait: TimeSpan.Zero);
        var component = context.Render<ScryPendingWork>(
            _ => _
                .Add(panel => panel.Store, client.PendingWork)
                .Add(panel => panel.AutoOpen, autoOpen));
        return (component, client, stub);
    }

    static async Task Until(Func<bool> reached)
    {
        var started = DateTime.UtcNow;
        while (!reached())
        {
            Assert.That(DateTime.UtcNow - started, Is.LessThan(patience), "Waited too long.");
            await Task.Delay(10);
        }
    }
}
