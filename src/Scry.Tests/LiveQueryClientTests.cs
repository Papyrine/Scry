using System.IO.Pipelines;

/// <summary>
/// What the client makes of a live query's connection: which events are answers, what ends it for
/// good and what is asked for again, and what each of the three ways of consuming one promises.
/// Driven by scripted responses, so that what is pinned is the client and not a server.
/// </summary>
[TestFixture]
public class LiveQueryClientTests
{
    [Test]
    public async Task EachResultIsAnAnswer()
    {
        var script = new Script(
            Result("a", "Alice") + Result("b", "Alice", "Bob") + End(reconnect: false));

        var answers = await Drain(script.Client());

        Assert.That(answers, Is.EqualTo(["Alice", "Alice,Bob"]));
    }

    [Test]
    public async Task HeartbeatsAndEventsFromANewerServerAreNotAnswers()
    {
        var script = new Script(
            ping + Result("a", "Alice") + "event: something-new\ndata: {}\n\n" + ping + End(reconnect: false));

        Assert.That(await Drain(script.Client()), Is.EqualTo(["Alice"]));
    }

    // A connection that stops without the server having said it was ending was cut. It is asked for
    // again, naming the last answer held, and the server saying that still stands yields nothing.
    [Test]
    public async Task ACutConnectionIsAskedForAgainNamingTheLastAnswer()
    {
        var script = new Script(
            Result("a", "Alice"),
            unchanged + Result("b", "Bob") + End(reconnect: false));

        var answers = await Drain(script.Client());

        Assert.Multiple(() =>
        {
            Assert.That(answers, Is.EqualTo(["Alice", "Bob"]));
            Assert.That(script.LastEventIds, Is.EqualTo([null, "a"]));
        });
    }

    [Test]
    public async Task AStreamTheServerEndedToBeAskedAgainIsAskedAgain()
    {
        var script = new Script(
            Result("a", "Alice") + End(reconnect: true),
            unchanged + End(reconnect: false));

        Assert.That(await Drain(script.Client()), Is.EqualTo(["Alice"]));
        Assert.That(script.LastEventIds, Is.EqualTo([null, "a"]));
    }

    [Test]
    public async Task ABusyOrFailingServerIsAskedAgain()
    {
        var script = new Script(
            Failure(HttpStatusCode.ServiceUnavailable, ScryErrorCode.SubscriptionLimit),
            Failure(HttpStatusCode.InternalServerError, ScryErrorCode.ExecutionFailed),
            Failure(HttpStatusCode.BadGateway, code: null),
            Result("a", "Alice") + End(reconnect: false));

        Assert.That(await Drain(script.Client()), Is.EqualTo(["Alice"]));
    }

    // Refused on the request's own merits: it would be refused again.
    [TestCase(HttpStatusCode.BadRequest, ScryErrorCode.Validation)]
    [TestCase(HttpStatusCode.BadRequest, ScryErrorCode.WireFormat)]
    [TestCase(HttpStatusCode.UnsupportedMediaType, ScryErrorCode.UnsupportedMedia)]
    public void ARefusalEndsTheLiveQuery(HttpStatusCode status, ScryErrorCode code)
    {
        var script = new Script(Failure(status, code), Result("a", "Alice"));

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => Drain(script.Client()));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Code, Is.EqualTo(code));
            Assert.That(script.Connections, Is.EqualTo(1));
        });
    }

    [Test]
    public void ADenialEndsTheLiveQuery()
    {
        var script = new Script(Failure(HttpStatusCode.Forbidden, ScryErrorCode.Forbidden));

        Assert.ThrowsAsync<ScryPermissionException>(() => Drain(script.Client()));
    }

    [Test]
    public void AStaleClientEndsTheLiveQuery()
    {
        var script = new Script(Failure(HttpStatusCode.BadRequest, ScryErrorCode.StaleClient));

        Assert.ThrowsAsync<ScryStaleClientException>(() => Drain(script.Client()));
    }

    // Said in the stream, because the status was long since sent — and surfaced exactly as the same
    // failure before the first answer would have been.
    [Test]
    public void AFailureSaidInTheStreamSurfacesAsTheStatusWouldHave()
    {
        var script = new Script(
            Result("a", "Alice") + Error("The result is larger than a live query may hold.", ScryErrorCode.Validation));

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => Drain(script.Client()));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Code, Is.EqualTo(ScryErrorCode.Validation));
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task AnExecutionFailureSaidInTheStreamIsAskedAgain()
    {
        var script = new Script(
            Result("a", "Alice") + Error("Query execution failed.", ScryErrorCode.ExecutionFailed),
            unchanged + End(reconnect: false));

        Assert.That(await Drain(script.Client()), Is.EqualTo(["Alice"]));
        Assert.That(script.Connections, Is.EqualTo(2));
    }

    // The route exists only where the server has said how many live queries it will hold.
    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.MethodNotAllowed)]
    public void AServerWithoutTheRouteSaysWhatToSet(HttpStatusCode status)
    {
        var script = new Script(new HttpResponseMessage(status));

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => Drain(script.Client()));

        Assert.That(exception!.Message, Does.Contain("MaxSubscriptions"));
    }

    // A single-page app's fallback route answers any unmapped path with its index page, and a 200.
    [Test]
    public void ASuccessThatIsNotAnEventStreamIsNotMistakenForOne()
    {
        var script = new Script(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!doctype html>", Encoding.UTF8, "text/html")
            });

        Assert.ThrowsAsync<NotSupportedException>(() => Drain(script.Client()));
    }

    [Test]
    public void APolicyThatDeclinesEndsTheLiveQueryWithWhatItWasRetrying()
    {
        var script = new Script(Failure(HttpStatusCode.BadGateway, code: null));
        var client = script.Client();
        client.Reconnect = new GivesUp();

        var exception = Assert.ThrowsAsync<ScryRequestException>(() => Drain(client));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
    }

    [Test]
    public async Task APolicyIsToldHowManyAttemptsHaveFailedInARow()
    {
        var script = new Script(
            Failure(HttpStatusCode.BadGateway, code: null),
            Failure(HttpStatusCode.BadGateway, code: null),
            Result("a", "Alice"),
            unchanged + End(reconnect: false));
        var client = script.Client();
        var policy = new Recording();
        client.Reconnect = policy;

        await Drain(client);

        // Counted up while nothing arrives, and from zero again once something has.
        Assert.That(policy.Counts, Is.EqualTo([0, 1, 0]));
    }

    [Test]
    public async Task TheRequestIsTheOneTheSameQueryAskedOnceSends()
    {
        var script = new Script(Result("a", "Alice") + End(reconnect: false));
        var client = script.Client();
        var names = Names(client);

        await Drain(client);

        Assert.Multiple(() =>
        {
            Assert.That(script.Bodies.Single(), Is.EqualTo(ScryJson.Serialize(names.ToScryRequest())));
            Assert.That(ScryJson.Serialize(names.Live().Request), Is.EqualTo(ScryJson.Serialize(names.ToScryRequest())));
            Assert.That(script.Paths.Single(), Is.EqualTo("/api/query/subscribe"));
        });
    }

    [Test]
    public async Task ACountIsReadAsACount()
    {
        var script = new Script(Scalar("a", 2) + Scalar("b", 3) + End(reconnect: false));
        List<int> counts = [];

        await foreach (var count in Names(script.Client()).LiveCount())
        {
            counts.Add(count);
        }

        Assert.That(counts, Is.EqualTo([2, 3]));
    }

    [Test]
    public async Task ASingleRowIsReadAsOneAndItsAbsenceAsNone()
    {
        var script = new Script(Single("a", "Alice") + Single("b", name: null) + End(reconnect: false));
        List<string?> names = [];

        await foreach (var row in Names(script.Client()).LiveFirstOrDefault())
        {
            names.Add(row?.Name);
        }

        Assert.That(names, Is.EqualTo(["Alice", null]));
    }

    [Test]
    public async Task ALongCountIsReadAsOne()
    {
        var large = (long) int.MaxValue + 1;
        var script = new Script(Scalar("a", large) + End(reconnect: false));
        List<long> counts = [];

        await foreach (var count in Names(script.Client()).LiveLongCount())
        {
            counts.Add(count);
        }

        Assert.Multiple(() =>
        {
            Assert.That(counts, Is.EqualTo([large]));
            Assert.That(Terminal(script), Is.EqualTo("longCount"));
        });
    }

    [Test]
    public async Task WhetherThereAreAnyIsReadAsABoolean()
    {
        var script = new Script(Scalar("a", true) + Scalar("b", false) + End(reconnect: false));
        List<bool> answers = [];

        await foreach (var any in Names(script.Client()).LiveAny())
        {
            answers.Add(any);
        }

        Assert.Multiple(() =>
        {
            Assert.That(answers, Is.EqualTo([true, false]));
            Assert.That(Terminal(script), Is.EqualTo("any"));
        });
    }

    // The predicate belongs to the terminal, as it does on the terminal asked once.
    [Test]
    public async Task APredicateGivenToALiveTerminalTravelsInIt()
    {
        var script = new Script(Scalar("a", true) + End(reconnect: false));

        await foreach (var _ in Names(script.Client()).LiveAny(_ => _.Name == "Alice"))
        {
        }

        using var request = JsonDocument.Parse(script.Bodies.Single());
        var terminal = request.RootElement.GetProperty("pipeline").EnumerateArray().Last();
        Assert.Multiple(() =>
        {
            Assert.That(terminal.GetProperty("$type").GetString(), Is.EqualTo("any"));
            Assert.That(terminal.GetProperty("predicate").ValueKind, Is.EqualTo(JsonValueKind.Object));
        });
    }

    [Test]
    public async Task TheOnlyRowIsReadAsOneAndItsAbsenceAsNone()
    {
        var script = new Script(Single("a", "Alice") + Single("b", name: null) + End(reconnect: false));
        List<string?> names = [];

        await foreach (var row in Names(script.Client()).LiveSingleOrDefault())
        {
            names.Add(row?.Name);
        }

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.EqualTo(["Alice", null]));
            Assert.That(Terminal(script), Is.EqualTo("single"));
        });
    }

    // A stream the server ended on schedule is asked for again at once; only a run of failures is
    // backed away from, and never further than the cap however long the outage.
    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(3, 4)]
    [TestCase(4, 8)]
    [TestCase(5, 16)]
    [TestCase(6, 30)]
    [TestCase(7, 30)]
    [TestCase(1000, 30)]
    [TestCase(int.MaxValue, 30)]
    public void TheDefaultPolicyDoublesToItsCapAndNeverGivesUp(int failures, int seconds)
    {
        var policy = new ScryClient((_, _) => throw new("never sent")).Reconnect;

        // Scattered, so asked often enough to see both ends of the scatter stay inside it.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var delay = policy.NextDelay(new(failures, TimeSpan.FromHours(1), RetryReason: null));

            Assert.That(delay, Is.Not.Null);
            Assert.That(
                delay!.Value.TotalSeconds,
                Is.InRange(seconds * 0.8, seconds * 1.2));
        }
    }

    // A server that restarts drops every live query it held at once. Without the scatter they would
    // all come back at once too.
    [Test]
    public void TheDefaultPolicyDoesNotSendEveryClientBackTogether()
    {
        var policy = new ScryClient((_, _) => throw new("never sent")).Reconnect;

        var delays = Enumerable.Range(0, 50)
            .Select(_ => policy.NextDelay(new(3, TimeSpan.Zero, RetryReason: null)))
            .Distinct()
            .Count();

        Assert.That(delays, Is.GreaterThan(1));
    }

    [Test]
    public void ABatchedQueryCannotBeLive()
    {
        var script = new Script();
        var client = script.Client();

        var exception = Assert.Throws<NotSupportedException>(() => Names(client).InBatch(client.Batch()).Live());

        Assert.That(exception!.Message, Does.Contain("batch"));
    }

    [Test]
    public void ATransportThatCannotHoldAQueryOpenSaysSo()
    {
        var client = new ScryClient((_, _) => throw new("never sent"));

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => Drain(client));

        Assert.That(exception!.Message, Does.Contain("subscribe transport"));
    }

    // A supplied transport has no names for its answers, so asking again gets the answer already
    // held sent again. The first of each connection is compared, and dropped where it says the same.
    [Test]
    public async Task ASuppliedTransportsRepeatedFirstAnswerIsNotDeliveredTwice()
    {
        var connections = 0;
        var client = new ScryClient(
            (_, _) => throw new("never sent"),
            subscribeTransport: (_, _) => connections++ switch
            {
                0 => Sequence(Rows("Alice")),
                1 => Sequence(Rows("Alice"), Rows("Alice", "Bob")),
                _ => throw new InvalidOperationException("Enough.")
            });

        List<string> answers = [];
        Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (var rows in Names(client).Live())
                {
                    answers.Add(string.Join(',', rows.Select(_ => _.Name)));
                }
            });

        Assert.That(answers, Is.EqualTo(["Alice", "Alice,Bob"]));
    }

    [Test]
    public async Task ACallbackIsHandedEachAnswerInOrder()
    {
        var script = new Script(Result("a", "Alice") + Result("b", "Bob") + End(reconnect: false));
        List<string> answers = [];

        await using var subscription = Names(script.Client())
            .Live()
            .Subscribe(rows => answers.Add(string.Join(',', rows.Select(_ => _.Name))));
        await subscription.Completion;

        Assert.Multiple(() =>
        {
            Assert.That(answers, Is.EqualTo(["Alice", "Bob"]));
            Assert.That(subscription.State, Is.EqualTo(ScrySubscriptionState.Closed));
            Assert.That(subscription.Error, Is.Null);
        });
    }

    // Bound to the Task overload, so the next answer waits for it and a failure in it is seen. As an
    // Action it would be async void, and neither would be true.
    [Test]
    public async Task AnAwaitingCallbackIsWaitedFor()
    {
        var script = new Script(Result("a", "Alice") + Result("b", "Bob") + End(reconnect: false));
        List<string> events = [];

        await using var subscription = Names(script.Client())
            .Live()
            .Subscribe(
                async rows =>
                {
                    events.Add($"begin {rows[0].Name}");
                    await Task.Delay(50);
                    events.Add($"end {rows[0].Name}");
                });
        await subscription.Completion;

        Assert.That(events, Is.EqualTo(["begin Alice", "end Alice", "begin Bob", "end Bob"]));
    }

    [Test]
    public async Task AFailureReachesTheErrorCallbackAndNeverTheCompletion()
    {
        var script = new Script(Failure(HttpStatusCode.Forbidden, ScryErrorCode.Forbidden));
        Exception? reported = null;

        await using var subscription = Names(script.Client())
            .Live()
            .Subscribe(_ => { }, exception => reported = exception);
        await subscription.Completion;

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.InstanceOf<ScryPermissionException>());
            Assert.That(subscription.Error, Is.SameAs(reported));
            Assert.That(subscription.State, Is.EqualTo(ScrySubscriptionState.Faulted));
        });
    }

    [Test]
    public async Task ACallbackThatThrowsEndsTheSubscriptionWithWhatItThrew()
    {
        var script = new Script(Result("a", "Alice") + Result("b", "Bob") + End(reconnect: false));
        var delivered = 0;

        await using var subscription = Names(script.Client())
            .Live()
            .Subscribe(
                _ =>
                {
                    delivered++;
                    throw new InvalidOperationException("The view is gone.");
                });
        await subscription.Completion;

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(subscription.Error, Is.InstanceOf<InvalidOperationException>());
        });
    }

    [Test]
    public async Task NothingIsDeliveredOnceDisposeHasReturned()
    {
        await using var body = new LiveBody();
        var script = new Script(body.Response());
        var delivered = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = Names(script.Client())
            .Live()
            .Subscribe(
                _ =>
                {
                    delivered++;
                    first.TrySetResult();
                });
        await body.Send(Result("a", "Alice"));
        await first.Task.WaitAsync(patience);

        subscription.Dispose();
        await body.Send(Result("b", "Bob"));
        await subscription.Completion.WaitAsync(patience);

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(subscription.State, Is.EqualTo(ScrySubscriptionState.Closed));
        });
    }

    [Test]
    public async Task TheStatesOfASubscriptionAreReported()
    {
        // The first connection says nothing until it is told to, so the handler is in place before
        // anything changes.
        var body = new LiveBody();
        var script = new Script(
            body.Response(),
            unchanged + End(reconnect: false));
        List<ScrySubscriptionState> states = [];

        var subscription = Names(script.Client()).Live().Subscribe(_ => { });
        subscription.StateChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };
        await body.Send(Result("a", "Alice"));

        // Cut, rather than ended: no closing event, so it is asked for again.
        await body.DisposeAsync();
        await subscription.Completion.WaitAsync(patience);

        // Connecting is where it starts, which is not a change.
        Assert.That(
            states,
            Is.EqualTo([ScrySubscriptionState.Live, ScrySubscriptionState.Reconnecting, ScrySubscriptionState.Live, ScrySubscriptionState.Closed]));
    }

    // A callback made on a UI thread is handed its answers there, and may touch what that thread owns.
    [Test]
    public async Task ACallbackIsDeliveredWhereItWasSubscribed()
    {
        var script = new Script(Result("a", "Alice") + End(reconnect: false));
        var context = new CountingContext();
        var previous = SynchronizationContext.Current;
        ScrySubscription subscription;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            subscription = Names(script.Client()).Live().Subscribe(_ => { });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await subscription.Completion;

        Assert.That(context.Posted, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task AnObserverIsGivenEachAnswerAndThenTheEnd()
    {
        var script = new Script(Result("a", "Alice") + Result("b", "Bob") + End(reconnect: false));
        var observer = new Observer();

        using var subscription = Names(script.Client()).Live().AsObservable().Subscribe(observer);
        await observer.Ended.WaitAsync(patience);

        Assert.That(observer.Calls, Is.EqualTo(["next Alice", "next Bob", "completed"]));
    }

    [Test]
    public async Task AnObserverIsGivenAFailureAndNothingAfterIt()
    {
        var script = new Script(Result("a", "Alice") + Error("Denied.", ScryErrorCode.Forbidden));
        var observer = new Observer();

        using var subscription = Names(script.Client()).Live().AsObservable().Subscribe(observer);
        await observer.Ended.WaitAsync(patience);

        Assert.That(observer.Calls, Is.EqualTo(["next Alice", "error ScryPermissionException"]));
    }

    // Each observer is a consumer of its own, with a connection of its own.
    [Test]
    public async Task EachObserverOpensAConnectionOfItsOwn()
    {
        var script = new Script(
            Result("a", "Alice") + End(reconnect: false),
            Result("a", "Alice") + End(reconnect: false));
        var observable = Names(script.Client()).Live().AsObservable();
        var first = new Observer();
        var second = new Observer();

        using var one = observable.Subscribe(first);
        using var two = observable.Subscribe(second);
        await first.Ended.WaitAsync(patience);
        await second.Ended.WaitAsync(patience);

        Assert.That(script.Connections, Is.EqualTo(2));
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    const string ping = "event: ping\ndata: \n\n";
    const string unchanged = "event: unchanged\ndata: \n\n";

    public class NameOnly
    {
        public string Name { get; set; } = "";
    }

    static IQueryable<NameOnly> Names(ScryClient client) =>
        client.Source<NameOnly>("Employee", ["Name"]);

    static async Task<List<string>> Drain(ScryClient client)
    {
        List<string> answers = [];
        await foreach (var rows in Names(client).Live())
        {
            answers.Add(string.Join(',', rows.Select(_ => _.Name)));
        }

        return answers;
    }

    static QueryResponse Rows(params string[] names) =>
        QueryResponse.Create(
            ResultKind.List,
            JsonSerializer.SerializeToElement(names.Select(_ => new {name = _})));

    static string Result(string id, params string[] names) =>
        Event(ScryLive.Result, id, ScryJson.Serialize(Rows(names)));

    static string? Terminal(Script script)
    {
        using var request = JsonDocument.Parse(script.Bodies.Single());
        return request.RootElement
            .GetProperty("pipeline")
            .EnumerateArray()
            .Last()
            .GetProperty("$type")
            .GetString();
    }

    static string Scalar<TValue>(string id, TValue value) =>
        Event(ScryLive.Result, id, ScryJson.Serialize(QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(value))));

    static string Single(string id, string? name) =>
        Event(
            ScryLive.Result,
            id,
            ScryJson.Serialize(
                QueryResponse.Create(
                    ResultKind.Single,
                    name is null
                        ? JsonSerializer.SerializeToElement<object?>(null)
                        : JsonSerializer.SerializeToElement(new {name}))));

    static string End(bool reconnect) =>
        Event(ScryLive.End, id: null, Encoding.UTF8.GetString(ScryJson.SerializeToUtf8(new ScryLiveEnd(reconnect))));

    static string Error(string message, ScryErrorCode code) =>
        Event(
            ScryLive.Error,
            id: null,
            ScryJson.Serialize(
                new ScryError(message)
                {
                    Code = code
                }));

    static string Event(string name, string? id, string data) =>
        id is null
            ? $"event: {name}\ndata: {data}\n\n"
            : $"event: {name}\nid: {id}\ndata: {data}\n\n";

    static HttpResponseMessage Failure(HttpStatusCode status, ScryErrorCode? code)
    {
        var body = code is { } known
            ? ScryJson.Serialize(
                new ScryError("refused")
                {
                    Code = known
                })
            : "<html>bad gateway</html>";
        return new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, code is null ? "text/html" : "application/json")
        };
    }

    static async IAsyncEnumerable<QueryResponse> Sequence(params QueryResponse[] answers)
    {
        foreach (var answer in answers)
        {
            yield return answer;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Answers each connection with the next scripted response, and records what each one asked.
    /// A connection past the end of the script is refused for good, so a test that reconnects more
    /// often than it meant to fails rather than spins.
    /// </summary>
    sealed class Script
    {
        Queue<HttpResponseMessage> responses = new();

        public Script(params object[] steps)
        {
            foreach (var step in steps)
            {
                responses.Enqueue(
                    step as HttpResponseMessage ??
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent((string)step, Encoding.UTF8, ScryLive.ContentType)
                    });
            }
        }

        public int Connections => LastEventIds.Count;

        public List<string?> LastEventIds { get; } = [];

        public List<string> Bodies { get; } = [];

        public List<string> Paths { get; } = [];

        public ScryClient Client()
        {
            var http = new HttpClient(new Handler(this))
            {
                BaseAddress = new("http://localhost")
            };
            var client = ScryClient.ForHttp(http, "/api/query");

            // No waiting between attempts: what is pinned is whether one is made.
            client.Reconnect = new Immediate();
            return client;
        }

        async Task<HttpResponseMessage> Respond(HttpRequestMessage request, Cancel cancel)
        {
            lock (responses)
            {
                LastEventIds.Add(
                    request.Headers.TryGetValues(ScryLive.LastEventIdHeader, out var values) ? values.Single() : null);
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            var body = await request.Content!.ReadAsStringAsync(cancel);
            lock (responses)
            {
                Bodies.Add(body);
                if (responses.TryDequeue(out var response))
                {
                    return response;
                }
            }

            return Failure(HttpStatusCode.BadRequest, ScryErrorCode.Validation);
        }

        sealed class Handler(Script script) :
            HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel) =>
                script.Respond(request, cancel);
        }
    }

    /// <summary>A response body written to while it is being read, as a live query's is.</summary>
    sealed class LiveBody :
        IAsyncDisposable
    {
        Pipe pipe = new();

        public HttpResponseMessage Response()
        {
            var content = new StreamContent(pipe.Reader.AsStream());
            content.Headers.ContentType = new(ScryLive.ContentType);
            return new(HttpStatusCode.OK)
            {
                Content = content
            };
        }

        public async Task Send(string text)
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
            await pipe.Writer.FlushAsync();
        }

        public ValueTask DisposeAsync() =>
            pipe.Writer.CompleteAsync();
    }

    sealed class Immediate :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            TimeSpan.Zero;
    }

    sealed class GivesUp :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            null;
    }

    sealed class Recording :
        IScryRetryPolicy
    {
        public List<int> Counts { get; } = [];

        public TimeSpan? NextDelay(ScryRetryContext context)
        {
            Counts.Add(context.PreviousRetryCount);
            return TimeSpan.Zero;
        }
    }

    sealed class CountingContext :
        SynchronizationContext
    {
        int posted;

        public int Posted => Volatile.Read(ref posted);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref posted);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    sealed class Observer :
        IObserver<IReadOnlyList<NameOnly>>
    {
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> calls = [];

        public Task Ended => ended.Task;

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (calls)
                {
                    return [.. calls];
                }
            }
        }

        public void OnNext(IReadOnlyList<NameOnly> value) =>
            Add($"next {string.Join(',', value.Select(_ => _.Name))}");

        public void OnError(Exception error)
        {
            Add($"error {error.GetType().Name}");
            ended.TrySetResult();
        }

        public void OnCompleted()
        {
            Add("completed");
            ended.TrySetResult();
        }

        void Add(string call)
        {
            lock (calls)
            {
                calls.Add(call);
            }
        }
    }
}
