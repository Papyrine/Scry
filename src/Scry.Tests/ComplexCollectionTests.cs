/// <summary>
/// A JSON array of value objects — a [QueryableCollection] whose element type is [QueryableComplex]
/// rather than a source. It behaves exactly like a collection of entities: aggregable, flattenable,
/// never projectable. What differs is only where the rows live, and that is EF's business, not the
/// wire's — the requests here are indistinguishable from the ones over Order.Lines.
/// </summary>
public class ComplexCollectionTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeRow(string Name, int Previous);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task AnyOverAJsonArray()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: clientComplexCollectionSubquery
        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.PreviousAddresses.Any(address => address.City == "Berlin"));
        // end-snippet

        // Alice and Carol have lived in Berlin.
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountOverAJsonArray()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.PreviousAddresses.Count))
            .ToListAsync();

        // Aaron's array is empty, which counts as zero rather than faulting.
        await Assert.That(rows.Select(_ => _.Previous)).IsEquivalentTo([0, 2, 1, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AllOverAJsonArray()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.PreviousAddresses.All(address => address.Country == "UK"));

        // Bob's one previous address is in the UK, and Aaron's empty array is vacuously true.
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task AggregateOverAJsonArrayInAPredicate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.PreviousAddresses.Max(address => address.City) == "Paris");

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task FlattensAJsonArray()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The flatten replaces the row being queried with the array's element, so the rest of the
        // pipeline is written against Address — which stands on its own allow-list.
        var cities = await client.Source<Employee>("Employee")
            .SelectMany(_ => _.PreviousAddresses)
            .Select(_ => new {_.City})
            .ToListAsync();

        await Assert.That(cities.Select(_ => _.City).Order()).IsEquivalentTo(["Berlin", "Berlin", "London", "London", "Paris"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FiltersTheRowsBeforeAndTheElementsAfterAFlatten()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var cities = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Carol")
            .SelectMany(_ => _.PreviousAddresses)
            .Where(_ => _.Country == "DE")
            .Select(_ => new {_.City})
            .ToListAsync();

        await Assert.That(cities.Single().City).IsEqualTo("Berlin");
    }

    [Test]
    public async Task AnIgnoredMemberStaysHiddenInsideAJsonArray()
    {
        using var context = TestContext.CreateSeeded();

        // Zip is [QueryIgnore]d on Address. EF still maps it, so it is physically in the JSON — the
        // allow-list is the only thing keeping it unreadable, and it applies to the array's element
        // exactly as it does when the same type is traversed as a single value.
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new SubqueryNode(
                    ["PreviousAddresses"],
                    SubqueryFn.Any,
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Zip"]),
                        new ConstNode("10115", ClrTypeTag.String))))
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).Contains("not allow-listed");
    }

    [Test]
    public void ProjectingAJsonArrayIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // Aggregable, never projectable — the bound on the response shape does not care that the rows
        // it would have carried were already sitting in the parent row's JSON.
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("PreviousAddresses", new NodeValue(new MemberNode(["PreviousAddresses"])))]))]);

        Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public void TraversingThroughAJsonArrayIsRejected()
    {
        using var context = TestContext.CreateSeeded();

        // No single element to read City from, same as any other collection.
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.Equal,
                    new MemberNode(["PreviousAddresses", "City"]),
                    new ConstNode("Berlin", ClrTypeTag.String)))
            ]);

        Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));
    }

    [Test]
    public async Task AttachingARowPolicyToAComplexTypeIsRefusedAtStartup()
    {
        // A policy filters a source, and a complex type has none — so one attached here would never
        // run, including over the JSON array a [QueryableCollection] of it exposes. The equivalent
        // mistake on an entity collection is already refused; this closes the same gap for a complex
        // one, where the existing check could never fire.
        var exception = Assert.ThrowsExactly<Exception>(
            () => ScryProcessor.Create<TestContext>(
                options =>
                {
                    options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                    options.AddPolicy<Address, UkAddressesOnlyPolicy>();
                }));

        await Assert.That(exception.Message).Contains("row policy");
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
