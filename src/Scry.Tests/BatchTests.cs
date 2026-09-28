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
    public async Task RejectedEntryLeavesOthersAnswered()
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

        await Assert.That(response.Results[0].Response).IsNotNull();
        await Assert.That(response.Results[1].Response).IsNull();
        await Assert.That(response.Results[2].Response).IsNotNull();
        await Verify(Pretty(ScryJson.Serialize(response)));
    }

    [Test]
    public async Task OverMaxBatchSizeRejectsTheWholeBatch()
    {
        var processor = Processor(_ => _.MaxBatchSize = 2);
        var batch = QueryBatchRequest.Create(
            [.. Enumerable.Repeat(QueryRequest.Create("Employee", [new CountOp()]), 3)]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(() => processor.ExecuteBatch(batch, context))!;

        await Assert.That(exception.Message).Contains("more than the maximum of 2");
    }

    // Refused at the envelope, the batch ran no entry, so nothing would have reached the trail. The
    // refusal is recorded once, carrying the batch rather than a query.
    [Test]
    public async Task ABatchRefusedWholeIsAuditedOnce()
    {
        var auditor = new RecordingAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IScryAuditor>(auditor);
        using var provider = services.BuildServiceProvider();
        var processor = Processor(_ => _.MaxBatchSize = 2);
        var batch = QueryBatchRequest.Create(
            [.. Enumerable.Repeat(QueryRequest.Create("Employee", [new CountOp()]), 3)]);

        using var context = TestContext.CreateSeeded();
        Assert.ThrowsExactly<ScryValidationException>(() => processor.ExecuteBatch(batch, context, provider));

        var entry = auditor.Entries.Single();
        using (Assert.Multiple())
        {
            await Assert.That(entry.Outcome).IsEqualTo(ScryQueryOutcome.Rejected);
            await Assert.That(entry.Batch).IsSameReferenceAs(batch);
            await Assert.That(entry.Request).IsNull();
            await Assert.That(entry.Error).Contains("more than the maximum of 2");
        }
    }

    [Test]
    public async Task UnsupportedWireVersionRejectsTheWholeBatch()
    {
        var batch = new QueryBatchRequest(WireFormat.Version + 1, [QueryRequest.Create("Employee", [new CountOp()])]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.ExecuteBatch(batch, context))!;

        await Assert.That(exception.Message).Contains("Unsupported wire version");
    }

    // An entry that fails at execution — a division by a constant of the client's choosing — reports
    // the fixed message and a 500, in its own slot, beside an entry that succeeded.
    [Test]
    public async Task AFailingEntryReportsTheFixedMessageInItsOwnSlot()
    {
        var failing = QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([
                    new("Ratio", new NodeValue(new BinaryNode(
                        BinaryOp.Divide,
                        new ConstNode("100", ClrTypeTag.Int32),
                        new BinaryNode(BinaryOp.Subtract, new MemberNode(["Id"]), new MemberNode(["Id"])))))
                ]))
            ]);
        var batch = QueryBatchRequest.Create([QueryRequest.Create("Employee", [new CountOp()]), failing]);

        using var context = TestContext.CreateSeeded();
        var response = SharedProcessor.Instance.ExecuteBatch(batch, context);

        using (Assert.Multiple())
        {
            await Assert.That(response.Results[0].Response).IsNotNull();
            await Assert.That(response.Results[0].Error).IsNull();
            await Assert.That(response.Results[1].Status).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(response.Results[1].Error).IsEqualTo("Query execution failed.");
            await Assert.That(response.Results[1].Response).IsNull();
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task AWireVersionBelowOneRejectsTheWholeBatch(int version)
    {
        var batch = new QueryBatchRequest(version, [QueryRequest.Create("Employee", [new CountOp()])]);

        using var context = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.ExecuteBatch(batch, context))!;

        await Assert.That(exception.Message).Contains("Unsupported wire version");
    }

    [Test]
    public async Task EveryEntryIsPolicyFiltered()
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

        await Assert.That(json).Contains("Alice");
        await Assert.That(json).DoesNotContain("Bob");
    }

    [Test]
    public async Task EveryEntryIsAuditedSeparately()
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
        await Assert.That(auditor.Entries).Count().IsEqualTo(2);
        await Assert.That(auditor.Entries[0].Outcome).IsEqualTo(ScryQueryOutcome.Success);
        await Assert.That(auditor.Entries[1].Outcome).IsEqualTo(ScryQueryOutcome.Rejected);
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

        await Assert.That(batch.Count).IsEqualTo(2);
        await Assert.That(batch.Sent).IsTrue();
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

        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(async () => await rejected))!;
        await Assert.That(exception.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(exception.Body).Contains("Unknown source 'Missing'");
        await Assert.That(await accepted).IsGreaterThan(0);
    }

    [Test]
    public async Task TransportFailureFaultsEveryEntry()
    {
        // A batch that never arrives must fault the entries rather than leave them pending: a caller
        // awaiting one would otherwise wait on a response that is never coming.
        var client = new ScryClient(
            (_, _) => throw new InvalidOperationException("no transport"),
            batchTransport: (_, _) => throw new InvalidOperationException("the batch failed"));

        var batch = client.Batch();
        var first = client.Source<Employee>("Employee").InBatch(batch).CountAsync();
        var second = client.Source<Employee>("Employee").InBatch(batch).CountAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => batch.SendAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await first);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await second);
    }

    [Test]
    public async Task SendingTwiceThrows()
    {
        await using var context = TestContext.CreateSeeded();
        var batch = BatchingClientFor(context).Batch();

        await batch.SendAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => batch.SendAsync());
    }

    [Test]
    public async Task AddingAfterSendThrows()
    {
        await using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        await batch.SendAsync();

        // Thrown by InBatch, not by the terminal: an async terminal would only surface it on await.
        Assert.ThrowsExactly<InvalidOperationException>(
            () => client.Source<Employee>("Employee").InBatch(batch));
    }

    [Test]
    public async Task HeadersInABatchAreRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        // One request carries the batch, so a query inside it has none of its own to write a header on.
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => client.Source<Employee>("Employee")
                .WithHeader("X-Trace", "1")
                .InBatch(batch))!;

        await Assert.That(exception.Message).Contains("Per-query headers cannot be used inside a batch");
    }

    [Test]
    public async Task StreamingInABatchIsRefused()
    {
        using var context = TestContext.CreateSeeded();
        var client = BatchingClientFor(context);
        var batch = client.Batch();

        var exception = (await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () =>
            {
                await foreach (var _ in client.Source<Employee>("Employee").InBatch(batch).ToAsyncEnumerable())
                {
                }
            }))!;

        await Assert.That(exception.Message).Contains("cannot be batched");
    }

    [Test]
    public async Task ATransportThatCannotBatchSaysSo()
    {
        // Mirrors the streaming rule: a transport with no batch support refuses rather than quietly
        // sending the queries one at a time and calling it a batch.
        var client = new ScryClient((_, _) => throw new InvalidOperationException("unused"));

        var exception = Assert.ThrowsExactly<NotSupportedException>(() => client.Batch())!;

        await Assert.That(exception.Message).Contains("does not batch");
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

    static JsonSerializerOptions indented =
        new()
        {
            WriteIndented = true
        };

    static string Pretty([StringSyntax(StringSyntaxAttribute.Json)] string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json), indented);
}
