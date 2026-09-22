/// <summary>
/// What a client says a caller may send: nothing until the server has answered, nothing where the
/// server serves no commands or cannot be asked, and never an exception — a button's enabled state is
/// no place for one.
/// </summary>
[TestFixture]
public class CapabilityClientTests
{
    [Test]
    public async Task NothingIsAllowedUntilTheServerAnswers()
    {
        var stub = new CommandStub
        {
            Capabilities = () => CommandStub.CapabilitiesOf("RenameThing")
        };
        var client = stub.Client();

        Assert.That(client.Can("RenameThing"), Is.False);
        await client.Ready;

        Assert.Multiple(() =>
        {
            Assert.That(client.Can("RenameThing"), Is.True);
            Assert.That(client.Can("CreateThing"), Is.False);
            Assert.That(stub.CapabilityReads, Is.EqualTo(1));
        });
    }

    // Where there is no context the change is said before the read completes, which is what lets this
    // count. Run without one, since NUnit runs a test under a context of its own, which runs what is
    // posted to it whenever the pool gets to it.
    [Test]
    public Task AChangeIsRaisedWhenTheAnswerMovesAndOnlyThen() =>
        WithoutContext(
            async () =>
            {
                var stub = new CommandStub
                {
                    Capabilities = () => CommandStub.CapabilitiesOf("RenameThing")
                };
                var client = stub.Client();
                var raised = 0;
                client.CapabilitiesChanged += () => Interlocked.Increment(ref raised);

                await client.Ready;
                await client.RefreshCapabilitiesAsync();
                Assert.That(raised, Is.EqualTo(1));

                stub.Capabilities = () => CommandStub.CapabilitiesOf("RenameThing", "CreateThing");
                await client.RefreshCapabilitiesAsync();

                Assert.Multiple(() =>
                {
                    Assert.That(raised, Is.EqualTo(2));
                    Assert.That(client.Can("CreateThing"), Is.True);
                });
            });

    [Test]
    public async Task AServerWithCommandsOffAllowsNothing()
    {
        var client = new CommandStub().Client();

        await client.Ready;

        Assert.That(client.Can("RenameThing"), Is.False);
    }

    [Test]
    public async Task AServerThatCannotBeAskedAllowsNothingAndThrowsNothing()
    {
        var stub = new CommandStub
        {
            Capabilities = () => throw new HttpRequestException("refused")
        };
        var client = stub.Client();

        await client.Ready;
        await client.RefreshCapabilitiesAsync();

        Assert.That(client.Can("RenameThing"), Is.False);
    }

    // Once allowed, a server that cannot be asked again leaves the last answer standing.
    [Test]
    public async Task AFailedRereadLeavesWhatWasKnown()
    {
        var stub = new CommandStub
        {
            Capabilities = () => CommandStub.CapabilitiesOf("RenameThing")
        };
        var client = stub.Client();
        await client.Ready;

        stub.Capabilities = () => new(HttpStatusCode.InternalServerError);
        await client.RefreshCapabilitiesAsync();

        Assert.That(client.Can("RenameThing"), Is.True);
    }

    [Test]
    public async Task AClientWithNoCapabilitiesTransportAllowsNothing()
    {
        var client = new ScryClient((_, _) => throw new NotSupportedException());

        await client.Ready;

        Assert.That(client.Can("RenameThing"), Is.False);
    }

    [Test]
    public async Task TheServersStampIsRecorded()
    {
        var content = new ByteArrayContent(ScryJson.SerializeToUtf8(CommandCapabilities.Create(["RenameThing"], "server-stamp")));
        content.Headers.ContentType = new("application/json");
        var stub = new CommandStub
        {
            Capabilities = () => new(HttpStatusCode.OK)
            {
                Content = content
            }
        };
        var client = stub.Client();

        await client.Ready;

        Assert.That(client.ServerSchemaStamp, Is.EqualTo("server-stamp"));
    }

    // Where the read started on a UI thread, the change is said there, so a component can redraw.
    [Test]
    public async Task AChangeIsRaisedWhereTheReadStarted()
    {
        var stub = new CommandStub
        {
            Capabilities = () => CommandStub.CapabilitiesOf("RenameThing")
        };
        var client = stub.Client();
        var context = new RecordingContext();
        var raisedOn = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.CapabilitiesChanged += () => raisedOn.TrySetResult(SynchronizationContext.Current);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            _ = client.Ready;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.That(await raisedOn.Task.WaitAsync(TimeSpan.FromSeconds(20)), Is.SameAs(context));
    }

    // Starts a test's body with no context, so that everything it awaits resumes without one, and puts
    // back whatever the runner had there before returning.
    static Task WithoutContext(Func<Task> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    sealed class RecordingContext :
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
