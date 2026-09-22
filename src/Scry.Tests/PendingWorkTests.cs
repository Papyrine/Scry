/// <summary>
/// The pending-work store: what goes into it, how long a finished command stays, and where it says so.
/// </summary>
[TestFixture]
public class PendingWorkTests
{
    static RenameThing Rename => new()
    {
        Id = 5,
        Name = "Renamed"
    };

    // A linger longer than the test, so what is checked is still there whenever the check runs.
    [Test]
    public async Task ACompletedCommandLingers()
    {
        var (client, held) = await Pending(linger: TimeSpan.FromMinutes(1));
        var store = client.PendingWork;
        var item = store.Items.Single();

        await held.Send(CommandStub.Result(CommandStatus.Completed));
        await Until(() => item.Status == ScryCommandStatus.Completed);

        Assert.Multiple(() =>
        {
            Assert.That(store.Items, Is.EqualTo([item]));
            Assert.That(store.PendingCount, Is.Zero);
            Assert.That(item.Finished, Is.Not.Null);
        });
        await held.DisposeAsync();
    }

    [Test]
    public async Task ACompletedCommandLeavesOnceItHasLingered()
    {
        var (client, held) = await Pending(linger: TimeSpan.FromMilliseconds(50));
        var store = client.PendingWork;

        await held.Send(CommandStub.Result(CommandStatus.Completed));

        await Until(() => store.Items.Count == 0);
        await held.DisposeAsync();
    }

    // A failure says something somebody should read, so it stays until they have.
    [Test]
    public async Task AFailedCommandStaysUntilCleared()
    {
        var (client, held) = await Pending(linger: TimeSpan.Zero);
        var store = client.PendingWork;
        var item = store.Items.Single();

        await held.Send(CommandStub.Result(CommandStatus.Failed, error: "No."));
        await Until(() => item.Status == ScryCommandStatus.Failed);
        await Task.Delay(50);

        Assert.Multiple(() =>
        {
            Assert.That(store.Items, Is.EqualTo([item]));
            Assert.That(item.Error, Is.EqualTo("No."));
        });

        store.ClearFinished();
        Assert.That(store.Items, Is.Empty);
        await held.DisposeAsync();
    }

    [Test]
    public async Task ClearingLeavesWhatIsStillPending()
    {
        var (client, held) = await Pending(linger: TimeSpan.Zero);

        client.PendingWork.ClearFinished();

        Assert.That(client.PendingWork.PendingCount, Is.EqualTo(1));
        await held.DisposeAsync();
    }

    // Where the command was sent from a UI thread, the store changes there and says so there.
    [Test]
    public async Task ChangesAreSaidWhereTheCommandWasSent()
    {
        await using var held = new HeldReceipts();
        var stub = new CommandStub(held.Step());
        var client = stub.Client(commandWait: TimeSpan.Zero);
        var context = new CountingContext();
        var contexts = new List<SynchronizationContext?>();
        client.PendingWork.Changed += () =>
        {
            lock (contexts)
            {
                contexts.Add(SynchronizationContext.Current);
            }
        };

        Task<ScryCommandOutcome> sending;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            sending = client.SendCommandAsync(Rename);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        var outcome = await sending;
        await held.Send(CommandStub.Result(CommandStatus.Completed));
        await outcome.Completion.WaitAsync(patience);
        await Until(
            () =>
            {
                lock (contexts)
                {
                    return contexts.Count >= 2;
                }
            });

        lock (contexts)
        {
            Assert.That(contexts, Is.All.SameAs(context));
        }
    }

    static async Task<(ScryClient Client, HeldReceipts Held)> Pending(TimeSpan linger)
    {
        var held = new HeldReceipts();
        var stub = new CommandStub(held.Step());
        var client = stub.Client(commandWait: TimeSpan.Zero);
        client.PendingWork.CompletedLinger = linger;
        var sending = client.SendCommandAsync(Rename);
        await Until(() => stub.Requests.Count == 1);
        await held.Send(CommandStub.Result(CommandStatus.Pending));
        var outcome = await sending;
        Assert.That(outcome.Status, Is.EqualTo(ScryCommandStatus.Pending));
        return (client, held);
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static async Task Until(Func<bool> reached)
    {
        var started = DateTime.UtcNow;
        while (!reached())
        {
            Assert.That(DateTime.UtcNow - started, Is.LessThan(patience), "Waited too long.");
            await Task.Delay(10);
        }
    }

    sealed class CountingContext :
        SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            ThreadPool.QueueUserWorkItem(
                _ =>
                {
                    SetSynchronizationContext(this);
                    callback(state);
                });
    }
}
