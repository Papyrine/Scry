/// <summary>
/// Streaming a list result. The rows are the same ones <c>ToListAsync</c> returns and go through the
/// same validation — a rejected query never reaches EF, so nothing has been written when a stream is
/// refused. What differs is that neither side holds the whole result.
/// </summary>
public class StreamingTests
{
    [Test]
    public async Task StreamsTheSameRowsAsAList()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new MemberNode(["Active"])),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

        var (begin, rows) = SharedProcessor.Instance.Stream(request, context);
        var streamed = await Read(rows);

        var listed = SharedProcessor.Instance.Execute(request, context);

        using (Assert.Multiple())
        {
            await Assert.That(begin.Kind).IsEqualTo(ScryStream.Begin);
            await Assert.That(begin.Stamp).IsEqualTo(SharedProcessor.Instance.SchemaStamp);
            await Assert.That(streamed.Select(_ => _["Name"])).IsEquivalentTo(new object?[] {"Aaron", "Alice", "Carol"}, CollectionOrdering.Matching);
            await Assert.That(streamed).Count().IsEqualTo(listed.Payload.GetArrayLength());
        }
    }

    [Test]
    public async Task AppliesTheRowPolicyToAStream()
    {
        await using var context = TestContext.CreateSeeded();

        // A policy filters the source before any client operator, and streaming changes nothing about
        // where it is applied.
        var request = QueryRequest.Create(
            "Ticket",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]);

        var (_, rows) = SharedProcessor.Instance.Stream(request, context);

        await Assert.That((await Read(rows)).Select(_ => _["Name"])).DoesNotContain("Old typo");
    }

    [Test]
    public async Task RejectsAStreamOfAScalarResult()
    {
        using var context = TestContext.CreateSeeded();

        // A folded terminal has one value, not rows. Refused before anything is written, so the
        // transport can still answer with a status rather than a half-sent stream.
        var request = QueryRequest.Create("Employee", [new CountOp(Predicate: null)]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Stream(request, context));

        await Assert.That(exception!.Message).Contains("Only a query that returns rows can be streamed");
    }

    [Test]
    public async Task RejectsADisallowedMemberBeforeStreaming()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Salary", new NodeValue(new MemberNode(["Salary"])))]))]);

        // Validation runs to completion before anything is rebound, so this is a rejection rather
        // than a stream that fails part-way.
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Stream(request, context));

        await Assert.That(exception!.Message).Contains("Salary");
    }

    [Test]
    public async Task EndsAStreamThatExceedsTheRowLimitWithAFailure()
    {
        using var context = TestContext.CreateSeeded();

        var processor = ScryProcessor.Create<TestContext>(
            options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.MaxStreamRows = 2;
            });

        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]);

        var (_, rows) = processor.Stream(request, context);

        // Four employees against a limit of two: the enumeration faults rather than stopping short,
        // so a reader cannot mistake the truncation for the end of the data.
        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(() => Read(rows));

        await Assert.That(exception!.Message).Contains("more than the maximum of 2 streamed rows");
    }

    [Test]
    public async Task StreamsAJoinedResult()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Employee",
            [
                new JoinOp(
                    "Department",
                    JoinKind.Inner,
                    new MemberNode(["DepartmentId"]),
                    new MemberNode(["Id"]),
                    InnerPredicate: null,
                    [
                        new("Employee", JoinSide.Outer, ["Name"]),
                        new("Department", JoinSide.Inner, ["Name"])
                    ])
            ]);

        var (_, rows) = SharedProcessor.Instance.Stream(request, context);

        await Assert.That(await Read(rows)).Count().IsEqualTo(4);
    }

    [Test]
    public async Task StreamsADeduplicatedResult()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Order",
            [
                new SelectOp(new([new("Region", new NodeValue(new MemberNode(["Region"])))])),
                new DistinctOp(),
                new OrderByOp(new MemberNode(["Region"]), Descending: false)
            ]);

        var (_, rows) = SharedProcessor.Instance.Stream(request, context);

        await Assert.That((await Read(rows)).Select(_ => _["Region"])).IsEquivalentTo(new object?[] {"North", "South"}, CollectionOrdering.Matching);
    }

    // An attachment is not a scalar, so it is absent from a streamed row exactly as it is from a
    // listed one. The row still carries the key the bytes are fetched by, which is what makes the
    // handle the client binds to it meaningful.
    [Test]
    public async Task StreamsARowCarryingAnAttachmentAsItsKeyOnly()
    {
        await using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create(
            "Contract",
            [new OrderByOp(new MemberNode(["Id"]), Descending: false)]);

        var (_, rows) = SharedProcessor.Instance.Stream(request, context);
        var streamed = await Read(rows);

        using (Assert.Multiple())
        {
            await Assert.That(streamed.Select(_ => _["Id"])).IsEquivalentTo(new object?[] {1, 2, UnsealedContractsPolicy.SealedId}, CollectionOrdering.Matching);
            await Assert.That(streamed.Select(_ => _["Name"])).IsEquivalentTo(new object?[] {"Lease", "Draft", "Sealed"}, CollectionOrdering.Matching);
            await Assert.That(streamed.Any(_ => _.ContainsKey("Document"))).IsFalse();
        }
    }

    static async Task<List<Dictionary<string, object?>>> Read(IAsyncEnumerable<Dictionary<string, object?>> rows)
    {
        var read = new List<Dictionary<string, object?>>();
        await foreach (var row in rows)
        {
            read.Add(row);
        }

        return read;
    }
}
