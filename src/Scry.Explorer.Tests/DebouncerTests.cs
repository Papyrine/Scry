// The trailing-edge debounce behind both the explorer's persist and its diagnostics pass. Its work
// runs on a task nothing awaits, so what it drops, what it lets through, and where a failure inside
// it surfaces are only observable here.
[TestFixture]
public class DebouncerTests
{
    // Short enough to keep the suite fast, long enough to be clear of scheduler jitter.
    const int Window = 30;

    static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RunsOnlyTheLastActionInTheWindow()
    {
        using var debouncer = new Debouncer(Window);
        var ran = new List<string>();
        var third = new TaskCompletionSource();

        debouncer.Run(() =>
        {
            ran.Add("first");
            return Task.CompletedTask;
        });
        debouncer.Run(() =>
        {
            ran.Add("second");
            return Task.CompletedTask;
        });
        debouncer.Run(() =>
        {
            ran.Add("third");
            third.SetResult();
            return Task.CompletedTask;
        });

        await third.Task.WaitAsync(Limit);

        Assert.That(ran, Is.EqualTo(["third"]));
    }

    // Not one-shot: once a window has closed, the next call opens a fresh one.
    [Test]
    public async Task RunsAgainOnceTheWindowHasClosed()
    {
        using var debouncer = new Debouncer(Window);
        var ran = 0;

        var first = new TaskCompletionSource();
        debouncer.Run(() =>
        {
            Interlocked.Increment(ref ran);
            first.SetResult();
            return Task.CompletedTask;
        });
        await first.Task.WaitAsync(Limit);

        var second = new TaskCompletionSource();
        debouncer.Run(() =>
        {
            Interlocked.Increment(ref ran);
            second.SetResult();
            return Task.CompletedTask;
        });
        await second.Task.WaitAsync(Limit);

        Assert.That(ran, Is.EqualTo(2));
    }

    // What the diagnostics pass is built on: a Roslyn run outlasts the window it started in, so the
    // action has to be able to tell that a later keystroke already superseded its result. The source
    // behind that token is disposed the moment it is superseded, so this covers the token staying
    // usable too — the cancellation lands first, which is what keeps it so.
    [Test]
    public async Task HandsTheActionATokenItsSuccessorCancels()
    {
        using var debouncer = new Debouncer(Window);
        var started = new TaskCompletionSource();
        var outcome = new TaskCompletionSource<string>();

        debouncer.Run(async cancel =>
        {
            started.SetResult();
            try
            {
                // Stands in for the slow pass: the next call lands while this is still running.
                await Task.Delay(Limit * 10, cancel);
                outcome.SetResult("ran to completion");
            }
            catch (OperationCanceledException)
            {
                outcome.SetResult("cancelled");
            }
            catch (Exception exception)
            {
                outcome.SetResult(exception.GetType().Name);
            }
        });

        await started.Task.WaitAsync(Limit);
        debouncer.Run(() => Task.CompletedTask);

        Assert.That(await outcome.Task.WaitAsync(Limit), Is.EqualTo("cancelled"));
    }

    [Test]
    public async Task DisposeDropsAPendingAction()
    {
        var ran = false;
        var debouncer = new Debouncer(Window);
        debouncer.Run(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        debouncer.Dispose();

        // Long enough that the window would have closed several times over had Dispose not shut it.
        await Task.Delay(Window * 10);

        Assert.That(ran, Is.False);
    }

    // Nothing awaits the debounced task, so an exception inside the action has nowhere to surface but
    // the console. Dropping it silently would leave an update that never happened looking exactly
    // like one that found nothing to do.
    [Test]
    public async Task ReportsAnActionThatThrewAndKeepsGoing()
    {
        using var debouncer = new Debouncer(Window);
        var reported = new StringWriter();
        var original = Console.Error;
        Console.SetError(reported);
        try
        {
            debouncer.Run(() => throw new InvalidOperationException("Deliberate."));
            await WaitUntil(
                () => reported.ToString().Contains("Deliberate."),
                "the failed action to be reported");

            var next = new TaskCompletionSource();
            debouncer.Run(() =>
            {
                next.SetResult();
                return Task.CompletedTask;
            });
            await next.Task.WaitAsync(Limit);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.That(reported.ToString(), Does.Contain("Scry: a debounced action failed."));
    }

    static async Task WaitUntil(Func<bool> condition, string expectation)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for {expectation}.");
    }
}
