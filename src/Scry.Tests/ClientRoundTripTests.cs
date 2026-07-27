namespace Scry.Tests;

[TestFixture]
public class ClientRoundTripTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record EmployeeRow(string Name, Status Status, string? ManagerName);

    record OrderSummary(string Region, decimal Total, int Count);

    record OrderRow(string Region, uint Quantity, ulong Sku);

    record EmployeeLocation(string Name, string City, string Country);

    record EmployeeCard(string Name, DepartmentCard Department);

    record DepartmentCard(string Name);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public Task ToScryRequestTranslatesWithoutExecuting()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Closure-captured values (wanted, prefix, take) force the translator's
        // Expression.Compile().DynamicInvoke() evaluation path — the same path the browser explorer
        // exercises in WebAssembly.
        var wanted = Status.FullTime;
        var prefix = "A";
        var take = 5;

        // begin-snippet: translateWithoutExecuting
        var request = client.Source<Employee>("Employee")
            .Where(_ => _.Active &&
                        _.Status == wanted &&
                        _.Name.StartsWith(prefix))
            .OrderBy(_ => _.Name)
            .Take(take)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToScryRequest();
        // end-snippet

        return Verify(request);
    }

    [Test]
    public async Task WhereOrderBySelect()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var prefix = "A";

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Status == Status.FullTime &&
                        _.Name.StartsWith(prefix))
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task GroupByAggregate()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .GroupBy(_ => _.Region)
            .Select(_ => new OrderSummary(_.Key, _.Sum(_ => _.Amount), _.Count()))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task ClosureCapturedConstant()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var wanted = Status.Contractor;

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Status == wanted)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task UnsignedMemberFilters()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        // uint/ulong literals have no dedicated ClrTypeTag; they ride the String tag and the server
        // reconciles them to the member's real type via Convert.ChangeType. This pins the round-trip
        // through EF/LocalDB, including a Sku above long.MaxValue that a numeric Int64 tag would break.
        var quantity = 7u;
        var sku = ulong.MaxValue;

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Quantity == quantity && _.Sku == sku)
            .Select(_ => new OrderRow(_.Region, _.Quantity, _.Sku))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task UnsignedMemberOrdering()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Order>("Order")
            .OrderByDescending(_ => _.Sku)
            .Select(_ => new OrderRow(_.Region, _.Quantity, _.Sku))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task ByteArrayEqualityFilter()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        // A closure-captured byte[] exercises ConstantOf's base64 encoding of ClrTypeTag.Bytes.
        var avatar = new byte[] { 0x01, 0x02, 0x03 };

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Avatar == avatar)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task CountTerminal()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .Where(_ => _.Active)
            .CountAsync();

        await Assert.ThatAsync(() => Task.FromResult(count), Is.EqualTo(3));
    }

    [Test]
    public async Task AnyTerminal()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var prefix = "Z";

        var any = await client.Source<Employee>("Employee")
            .Where(_ => _.Name.StartsWith(prefix))
            .AnyAsync();

        Assert.That(any, Is.False);
    }

    [Test]
    public async Task CollectionShapingTerminals()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var query = client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name));

        // Every terminal sends the same list request and reshapes the four seeded rows client-side.
        var array = await query.ToArrayAsync();
        var hashSet = await query.ToHashSetAsync();
        var byName = await query.ToDictionaryAsync(_ => _.Name);
        var namesByStatus = await query.ToDictionaryAsync(_ => _.Name, _ => _.Status);
        var byStatus = await query.ToLookupAsync(_ => _.Status);

        Assert.Multiple(() =>
        {
            Assert.That(array.Select(_ => _.Name), Is.EqualTo(["Aaron", "Alice", "Bob", "Carol"]));
            Assert.That(hashSet, Has.Count.EqualTo(4));
            Assert.That(byName.Keys, Is.EquivalentTo(["Aaron", "Alice", "Bob", "Carol"]));
            Assert.That(namesByStatus["Alice"], Is.EqualTo(Status.FullTime));
            Assert.That(byStatus[Status.FullTime].Select(_ => _.Name), Is.EquivalentTo(["Aaron", "Alice"]));
        });
    }

    [Test]
    public async Task PagingWithSkipReportsHasMore()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Ordered by Name the four seeded employees are Aaron, Alice, Bob, Carol. A page size of 2
        // yields the first two with HasMore, then Skip(2) advances to the last two with HasMore false.
        var first = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToPageAsync(2);

        var second = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Skip(2)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToPageAsync(2);

        Assert.Multiple(() =>
        {
            Assert.That(first.Items.Select(_ => _.Name), Is.EqualTo(["Aaron", "Alice"]));
            Assert.That(first.HasMore, Is.True);
            // Cursor is null until keyset paging (slice 2); offset paging advances with Skip.
            Assert.That(first.Cursor, Is.Null);
            Assert.That(second.Items.Select(_ => _.Name), Is.EqualTo(["Bob", "Carol"]));
            Assert.That(second.HasMore, Is.False);
        });
    }

    [Test]
    public void ToAsyncEnumerableNotSupportedYet()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Streaming is a planned enhancement; the terminal throws rather than silently buffering.
        Assert.Throws<NotSupportedException>(() =>
            client.Source<Employee>("Employee").ToAsyncEnumerable());
    }

    [Test]
    public async Task ComplexTypeTraversal()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Filter through and project scalar leaves of the JSON-mapped Address complex type. The server
        // rebinds Address.City/Country onto EF, which translates them into the JSON column.
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Address.Country == "UK")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeLocation(_.Name, _.Address.City, _.Address.Country))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task NestedProjectionIntoNavigation()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Projecting into the Department navigation produces a nested result object rather than a
        // flattened column — the client emits a NestedValue and the row shape is { Name, Department: { Name } }.
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name)))
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public void UnsupportedProjectionThrows()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Employee>("Employee")
                .Select(_ => _.Name)
                .ToListAsync());
    }

    // begin-snippet: inProcessClient
    static ScryClient ClientFor(TestContext context)
    {
        var processor = ScryProcessor.Create<TestContext>(
            _ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

        return new((request, _) => Task.FromResult(processor.Execute(request, context)));
    }
    // end-snippet
}
