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

        await Verify(rows)
            .Snapshot(
                """
                [
                  {
                    Name: Aaron,
                    ManagerName: Alice
                  },
                  {
                    Name: Alice
                  }
                ]
                """);
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

        await Verify(rows)
            .Snapshot(
                """
                [
                  {
                    Name: Carol,
                    Status: Contractor
                  }
                ]
                """);
    }

    [Test]
    public async Task UnsignedMemberFilters()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        // uint/ulong literals have no dedicated ClrTypeTag; they use the String tag and the server
        // reconciles them to the member's real type via Convert.ChangeType. This pins the round-trip
        // through EF/LocalDB, including a Sku above long.MaxValue that a numeric Int64 tag would break.
        var quantity = 7u;
        var sku = ulong.MaxValue;

        var rows = await client.Source<Order>("Order")
            .Where(_ => _.Quantity == quantity && _.Sku == sku)
            .Select(_ => new OrderRow(_.Region, _.Quantity, _.Sku))
            .ToListAsync();

        await Verify(rows)
            .Snapshot(
                """
                [
                  {
                    Region: North,
                    Quantity: 7,
                    Sku: 18446744073709551615
                  }
                ]
                """);
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

        await Verify(rows)
            .Snapshot(
                """
                [
                  {
                    Name: Alice
                  }
                ]
                """);
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
            Assert.That(second.Items.Select(_ => _.Name), Is.EqualTo(["Bob", "Carol"]));
            Assert.That(second.HasMore, Is.False);
            // The Skip page is offset paging, so it carries no cursor; the unskipped page is seek-safe.
            Assert.That(second.Cursor, Is.Null);
        });
    }

    [Test]
    public async Task KeysetPagingWithCursor()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Ordered by Name: Aaron, Alice, Bob, Carol. Page 2 resumes past page 1 via the returned cursor
        // (a keyset seek), not an offset.
        IQueryable<EmployeeRow> Ordered() => client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name));

        var first = await Ordered().ToPageAsync(2);
        var second = await Ordered().ToPageAsync(2, first.Cursor);

        string[] firstPage = ["Aaron", "Alice"];
        string[] secondPage = ["Bob", "Carol"];
        Assert.Multiple(() =>
        {
            Assert.That(first.Items.Select(_ => _.Name), Is.EqualTo(firstPage));
            Assert.That(first.HasMore, Is.True);
            Assert.That(first.Cursor, Is.Not.Null);
            Assert.That(second.Items.Select(_ => _.Name), Is.EqualTo(secondPage));
            Assert.That(second.HasMore, Is.False);
            // Last page — nothing left to resume.
            Assert.That(second.Cursor, Is.Null);
        });
    }

    // Guid and bool define no relational operator, so the seek past a cursor cannot be written as a
    // plain comparison. Each is exercised on the database, since the answer is the provider's.
    [Test]
    public async Task KeysetPagingOverAGuidKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        IQueryable<TicketName> Ordered() => client.Source<Ticket>("Ticket")
            .OrderBy(_ => _.Token)
            .Select(_ => new TicketName(_.Name));

        var first = await Ordered().ToPageAsync(1);
        var second = await Ordered().ToPageAsync(1, first.Cursor);

        // The policy hides the closed ticket, so the two open ones are the whole result.
        string[] open = ["Login bug", "Signup crash"];
        Assert.Multiple(() =>
        {
            Assert.That(first.Cursor, Is.Not.Null);
            Assert.That(first.HasMore, Is.True);
            Assert.That(second.HasMore, Is.False);
            Assert.That(first.Items.Concat(second.Items).Select(_ => _.Name), Is.EquivalentTo(open));
        });
    }

    [Test]
    public async Task KeysetPagingOverABoolKey()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Ordered by Active alone; the primary key is appended as the tiebreaker, so every page seeks
        // past a bool and an int together.
        IQueryable<EmployeeRow> Ordered() => client.Source<Employee>("Employee")
            .OrderBy(_ => _.Active)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name));

        var names = new List<string>();
        var page = await Ordered().ToPageAsync(2);
        names.AddRange(page.Items.Select(_ => _.Name));
        while (page.HasMore)
        {
            page = await Ordered().ToPageAsync(2, page.Cursor);
            names.AddRange(page.Items.Select(_ => _.Name));
        }

        string[] all = ["Aaron", "Alice", "Bob", "Carol"];
        Assert.That(names, Is.EquivalentTo(all));
    }

    [Test]
    public async Task PagingOverAByteArrayKeyIssuesNoCursor()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // No comparison can seek past a byte[], so the page is answered without a cursor rather than
        // with one the next page would fault on.
        var page = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Avatar)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToPageAsync(2);

        Assert.Multiple(() =>
        {
            Assert.That(page.Items, Has.Count.EqualTo(2));
            Assert.That(page.HasMore, Is.True);
            Assert.That(page.Cursor, Is.Null);
        });
    }

    record TicketName(string Name);

    [Test]
    public async Task KeysetPagingMultiKeyVisitsEveryRowOnce()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A composite order (Status, then Name) exercises the multi-key lexicographic seek plus the
        // appended primary-key tiebreaker. Paging all the way through must visit every row exactly once.
        IQueryable<EmployeeRow> Ordered() => client.Source<Employee>("Employee")
            .OrderBy(_ => _.Status)
            .ThenBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name));

        var names = new List<string>();
        var page = await Ordered().ToPageAsync(2);
        names.AddRange(page.Items.Select(_ => _.Name));
        while (page.HasMore)
        {
            page = await Ordered().ToPageAsync(2, page.Cursor);
            names.AddRange(page.Items.Select(_ => _.Name));
        }

        // Status order is FullTime, PartTime, Contractor; the two full-timers (by Name) come first.
        string[] all = ["Aaron", "Alice", "Bob", "Carol"];
        Assert.That(names, Is.EqualTo(all));
    }

    [Test]
    public async Task KeysetNullableKeyFallsBackToOffset()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // ManagerId is nullable, so the ordering is not a safe total order for a cursor: the page is
        // served by offset with no cursor emitted.
        var page = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.ManagerId)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name))
            .ToPageAsync(2);

        Assert.Multiple(() =>
        {
            Assert.That(page.Items, Has.Count.EqualTo(2));
            Assert.That(page.HasMore, Is.True);
            Assert.That(page.Cursor, Is.Null);
        });
    }

    [Test]
    public void ToAsyncEnumerableNeedsATransportThatStreams()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // This client is built over a single-response transport. Rather than quietly buffering the
        // whole result and calling it a stream, the terminal says so.
        var exception = Assert.ThrowsAsync<NotSupportedException>(
            async () =>
            {
                await foreach (var _ in client.Source<Employee>("Employee").ToAsyncEnumerable())
                {
                    Assert.Fail("No row should arrive over a transport that cannot stream.");
                }
            });

        Assert.That(exception!.Message, Does.Contain("does not stream"));
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

    // A method outside the callable set that reads the row has nowhere to run. The refusal names it,
    // since the translator is the only reporter for a query the analyzer could not see into.
    [Test]
    public void ClientSideCallIsNamedInTheRefusal()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() =>
            client.Source<Employee>("Employee")
                .Where(_ => Munge(_.Name) == "x")
                .Select(_ => new {_.Name})
                .ToListAsync());

        Assert.That(exception!.Message, Does.Contain("ClientRoundTripTests.Munge"));
        Assert.That(exception.Message, Does.Contain("client-side"));
    }

    static string Munge(string value) => value;

    // begin-snippet: inProcessClient
    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
    // end-snippet
}
