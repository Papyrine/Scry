using System.Diagnostics.CodeAnalysis;
using System.Net;

[TestFixture]
public class BatchTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeRow(string Name, Status Status);

    record OrderRow(string Region, decimal Amount);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public Task MixedResultKinds()
    {
        // One batch carrying a list, a scalar, and a page: every result kind keeps the shape it has
        // when sent alone, so a batched response is the single-query responses side by side.
        var batch = QueryBatchRequest.Create(
        [
            QueryRequest.Create(
                "Employee",
                [
                    new WhereOp(new MemberNode(["Active"])),
                    new OrderByOp(new MemberNode(["Name"]), Descending: false),
                    new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
                ]),
            QueryRequest.Create("Order", [new CountOp()]),
            QueryRequest.Create(
                "Employee",
                [
                    new OrderByOp(new MemberNode(["Name"]), Descending: false),
                    new PageOp(Size: 2)
                ])
        ]);

        using var context = TestContext.CreateSeeded();
        var response = SharedProcessor.Instance.ExecuteBatch(batch, context);
        return Verify(Pretty(ScryJson.Serialize(response)));
    }

    [Test]
    public Task RejectedEntryLeavesOthersAnswered()
    {
        // The property that makes a batch safe to use: entries are independent, so one asking for a
        // [QueryIgnore]d member is rejected on its own and the queries around it still answer.
        var batch = QueryBatchRequest.Create(
        [
            QueryRequest.Create("Order", [new CountOp()]),
            QueryRequest.Create("Employee", [new WhereOp(new MemberNode(["Salary"]))]),
            QueryRequest.Create("Employee", [new CountOp()])
        ]);

        using var context = TestContext.CreateSeeded();
        var response = SharedProcessor.Instance.ExecuteBatch(batch, context);

        Assert.That(response.Results[0].Response, Is.Not.Null);
        Assert.That(response.Results[1].Response, Is.Null);
        Assert.That(response.Results[2].Response, Is.Not.Null);
        return Verify(Pretty(ScryJson.Serialize(response)));
    }

    [Test]
    public void OverMaxBatchSizeRejectsTheWholeBatch()
    {
        var processor = Processor(_ => _.MaxBatchSize = 2);
        var batch = QueryBatchRequest.Create(
            [.. Enumerable.Repeat(QueryRequest.Create("Employee", [new CountOp()]), 3)]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(() => processor.ExecuteBatch(batch, context))!;

        Assert.That(exception.Message, Does.Contain("more than the maximum of 2"));
    }

    // Refused at the envelope, the batch ran no entry, so nothing would have reached the trail. The
    // refusal is recorded once, carrying the batch rather than a query.
    [Test]
    public void ABatchRefusedWholeIsAuditedOnce()
    {
        var auditor = new RecordingAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IScryAuditor>(auditor);
        using var provider = services.BuildServiceProvider();
        var processor = Processor(_ => _.MaxBatchSize = 2);
        var batch = QueryBatchRequest.Create(
            [.. Enumerable.Repeat(QueryRequest.Create("Employee", [new CountOp()]), 3)]);

        using var context = TestContext.CreateSeeded();
        Assert.Throws<ScryValidationException>(() => processor.ExecuteBatch(batch, context, provider));

        var entry = auditor.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Outcome, Is.EqualTo(ScryQueryOutcome.Rejected));
            Assert.That(entry.Batch, Is.SameAs(batch));
            Assert.That(entry.Request, Is.Null);
            Assert.That(entry.Error, Does.Contain("more than the maximum of 2"));
        });
    }

    [Test]
    public void UnsupportedWireVersionRejectsTheWholeBatch()
    {
        var batch = new QueryBatchRequest(WireFormat.Version + 1, [QueryRequest.Create("Employee", [new CountOp()])]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.ExecuteBatch(batch, context))!;

        Assert.That(exception.Message, Does.Contain("Unsupported wire version"));
    }

    [Test]
    public void EveryEntryIsPolicyFiltered()
    {
        // A row policy has to narrow each entry of a batch exactly as it narrows a lone query —
        // otherwise batching would be a way around one. Bob is inactive, so no entry may return him.
        var processor = Processor(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>());
        var batch = QueryBatchRequest.Create(
        [
            QueryRequest.Create("Employee", [new OrderByOp(new MemberNode(["Name"]), Descending: false)]),
            QueryRequest.Create("Employee", [new CountOp()])
        ]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(processor.ExecuteBatch(batch, context));

        Assert.That(json, Does.Contain("Alice"));
        Assert.That(json, Does.Not.Contain("Bob"));
    }

    [Test]
    public void EveryEntryIsAuditedSeparately()
    {
        var auditor = new RecordingAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IScryAuditor>(auditor);
        using var provider = services.BuildServiceProvider();

        var batch = QueryBatchRequest.Create(
        [
            QueryRequest.Create("Employee", [new CountOp()]),
            QueryRequest.Create("Employee", [new WhereOp(new MemberNode(["Salary"]))])
        ]);

        using var context = TestContext.CreateSeeded();
        SharedProcessor.Instance.ExecuteBatch(batch, context, provider);

        // A batch is not one audit entry: the trail records what was asked, and a batch asked twice.
        Assert.That(auditor.Entries, Has.Count.EqualTo(2));
        Assert.That(auditor.Entries[0].Outcome, Is.EqualTo(ScryQueryOutcome.Success));
        Assert.That(auditor.Entries[1].Outcome, Is.EqualTo(ScryQueryOutcome.Rejected));
    }

    [Test]
    public async Task ClientTerminalsCompleteOnSend()
    {
        await using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);

        // begin-snippet: clientBatch
        var batch = client.Batch();

        // Each terminal returns a task that completes when the batch is sent — so collect them first,
        // then send, then await. Awaiting one before SendAsync would wait forever.
        var employees = client.Source<Employee>("Employee")
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status))
            .InBatch(batch)
            .ToListAsync();

        var orders = client.Source<Order>("Order")
            .InBatch(batch)
            .CountAsync();

        await batch.SendAsync();

        var rows = await employees;
        var count = await orders;
        // end-snippet

        Assert.That(batch.Count, Is.EqualTo(2));
        Assert.That(batch.Sent);
        await Verify(new {rows, count});
    }

    [Test]
    public async Task RejectedEntryFaultsOnlyItsOwnTask()
    {
        await using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        // "Missing" is not an allow-listed source, so this entry is rejected while the other answers.
        var rejected = client.Source<Employee>("Missing")
            .InBatch(batch)
            .CountAsync();
        var accepted = client.Source<Employee>("Employee")
            .InBatch(batch)
            .CountAsync();

        await batch.SendAsync();

        var exception = Assert.ThrowsAsync<ScryRequestException>(async () => await rejected)!;
        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(exception.Body, Does.Contain("Unknown source 'Missing'"));
        Assert.That(await accepted, Is.GreaterThan(0));
    }

    [Test]
    public void TransportFailureFaultsEveryEntry()
    {
        // A batch that never arrives must fault the entries rather than leave them pending: a caller
        // awaiting one would otherwise wait on a response that is never coming.
        var client = new ScryClient(
            (_, _) => throw new InvalidOperationException("no transport"),
            batchTransport: (_, _) => throw new InvalidOperationException("the batch failed"));

        var batch = client.Batch();
        var first = client.Source<Employee>("Employee").InBatch(batch).CountAsync();
        var second = client.Source<Employee>("Employee").InBatch(batch).CountAsync();

        Assert.ThrowsAsync<InvalidOperationException>(() => batch.SendAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await first);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await second);
    }

    [Test]
    public async Task SendingTwiceThrows()
    {
        await using var context = TestContext.CreateSeeded();
        var batch = BatchingClientFor(context).Batch();

        await batch.SendAsync();

        Assert.ThrowsAsync<InvalidOperationException>(() => batch.SendAsync());
    }

    [Test]
    public async Task AddingAfterSendThrows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        await batch.SendAsync();

        // Thrown by InBatch, not by the terminal: an async terminal would only surface it on await.
        Assert.Throws<InvalidOperationException>(
            () => client.Source<Employee>("Employee").InBatch(batch));
    }

    [Test]
    public void HeadersInABatchAreRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        // One request carries the batch, so a query inside it has none of its own to write a header on.
        var exception = Assert.Throws<NotSupportedException>(
            () => client.Source<Employee>("Employee")
                .WithHeader("X-Trace", "1")
                .InBatch(batch))!;

        Assert.That(exception.Message, Does.Contain("Per-query headers cannot be used inside a batch"));
    }

    [Test]
    public void StreamingInABatchIsRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        var exception = Assert.ThrowsAsync<NotSupportedException>(
            async () =>
            {
                await foreach (var _ in client.Source<Employee>("Employee").InBatch(batch).ToAsyncEnumerable())
                {
                }
            })!;

        Assert.That(exception.Message, Does.Contain("cannot be batched"));
    }

    [Test]
    public void ATransportThatCannotBatchSaysSo()
    {
        // Mirrors the streaming rule: a transport with no batch support refuses rather than quietly
        // sending the queries one at a time and calling it a batch.
        var client = new ScryClient((_, _) => throw new InvalidOperationException("unused"));

        var exception = Assert.Throws<NotSupportedException>(() => client.Batch())!;

        Assert.That(exception.Message, Does.Contain("does not batch"));
    }

    static ScryClient BatchingClientFor(TestContext context) =>
        new(
            (request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)),
            batchTransport: (request, _) => Task.FromResult(SharedProcessor.Instance.ExecuteBatch(request, context)));

    static ScryProcessor Processor(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                extra(options);
            });

    sealed class RecordingAuditor :
        IScryAuditor
    {
        public List<ScryAuditEntry> Entries { get; } = [];

        public void Record(ScryAuditEntry entry) =>
            Entries.Add(entry);
    }

    static readonly JsonSerializerOptions indented =
        new()
        {
            WriteIndented = true
        };

    static string Pretty([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json), indented);
}
