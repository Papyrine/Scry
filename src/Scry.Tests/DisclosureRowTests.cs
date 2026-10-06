/// <summary>
/// What the disclosure audit knows about an answer beyond its bytes: which rows each row of it was
/// read from, by keys the query never asked for and the caller is never sent, and which members of
/// which sources the query read, each with what it did with it.
/// </summary>
public class DisclosureRowTests
{
    static IQueryable<Employee> Employees => Disclosures.From<Employee>("Employee");

    static IQueryable<Order> Orders => Disclosures.From<Order>("Order");

    // The request names one member. The record says which rows the three names came from all the
    // same: the key is read beside what was asked for, and never written.
    [Test]
    public async Task ARowIsRecordedByAKeyItWasNeverSentWith()
    {
        var (processor, store) = Disclosures.Audited();

        var answer = await Disclosures.Buffered(processor, Disclosures.Names());

        await Assert.That(Encoding.UTF8.GetString(answer)).DoesNotContain("id").IgnoringCase();
        await Verify(await Disclosures.Shape(store));
    }

    // A row that carries something of another row is recorded as both: the employee, the manager
    // whose name came with them, the department whose name did. A navigation that led nowhere — Alice
    // has no manager — is recorded as nothing, because nothing of anybody was sent.
    [Test]
    public async Task ARowReachedThroughANavigationIsRecordedToo()
    {
        var (processor, store) = Disclosures.Audited();
        var request = Employees
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                Manager = _.Manager!.Name,
                ManagerDepartment = _.Manager!.Department!.Name,
                Department = new
                {
                    _.Department!.Name
                }
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);

        await Verify(await Disclosures.Shape(store));
    }

    // Stepping through a row to filter by it is not sending it: the department decides which
    // employees are listed and is no row of the answer.
    [Test]
    public async Task ARowOnlyFilteredThroughIsNotARowOfTheAnswer()
    {
        var (processor, store) = Disclosures.Audited();
        var request = Employees
            .Where(_ => _.Department!.Name == "Sales")
            .OrderBy(_ => _.Manager!.Name)
            .Select(_ => new
            {
                _.Name
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);

        await Verify(await Disclosures.Shape(store));
    }

    // A navigation into a source with a row policy is read through that policy. A row it hides comes
    // back as nothing, and is recorded as nothing: Carol's department is hidden, so her row is hers
    // alone.
    [Test]
    public async Task ARowAPolicyHidesBehindANavigationIsNotRecorded()
    {
        var (processor, store) = Disclosures.Audited(_ => _.AddPolicy<Department, EngineeringOnly>());
        var request = Employees
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                Department = _.Department!.Name
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);

        await Verify(await Disclosures.Shape(store));
    }

    [Test]
    public async Task AJoinedRowIsARowOfEachSide()
    {
        var (processor, store) = Disclosures.Audited();
        var request = Employees
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Join(
                Disclosures.From<Department>("Department"),
                _ => _.DepartmentId,
                _ => _.Id,
                (employee, department) => new
                {
                    Employee = employee.Name,
                    Department = department.Name
                })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);

        await Verify(await Disclosures.Shape(store));
    }

    // A row read as a subtype is the row it is in the database: recorded under the top of its
    // hierarchy, so asking about an asset finds it whichever way it was read.
    [Test]
    public async Task ARowReadAsASubtypeIsRecordedUnderItsHierarchy()
    {
        var (processor, store) = Disclosures.Audited();
        var request = Disclosures.From<Asset>("Asset")
            .OfType<Vehicle>()
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                _.Wheels
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);

        await Verify(await Disclosures.Shape(store));
    }

    // Flattened, the rows are the collection's: each line is a row of its own source, with its own key.
    [Test]
    public async Task AFlattenedRowIsARowOfTheCollection()
    {
        var (processor, store) = Disclosures.Audited();
        var request = Orders
            .Where(_ => _.Region == "North")
            .SelectMany(_ => _.Lines)
            .OrderBy(_ => _.Sku)
            .Select(_ => new
            {
                _.Sku
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);

        await Verify(await Disclosures.Shape(store));
    }

    // A value a row holds is part of that row, so its members are named against the row: an address
    // is the employee's, wherever in the query it was read, and has no key of its own to record.
    [Test]
    public async Task AValueARowHoldsIsNamedAgainstTheRow()
    {
        var (processor, store) = Disclosures.Audited();
        var held = Employees
            .Where(_ => _.Address.Country == "UK")
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                _.Address.City,
                Past = _.PreviousAddresses.Count(address => address.Country == "DE")
            })
            .ToScryRequest();
        var flattened = Employees
            .SelectMany(_ => _.PreviousAddresses)
            .Where(_ => _.Country == "DE")
            .Select(_ => new
            {
                _.City
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, held);
        var first = await Disclosures.Shape(store);
        await Disclosures.Buffered(processor, flattened);

        await Verify(
            new
            {
                Held = first,
                Flattened = await Disclosures.Shape(store)
            });
    }

    // Rows folded together are rows of nothing: a group, a deduplicated value and a count name no row.
    // What they were made from is still recorded, and how — a member grouped by and returned, one
    // summed, one only counted over.
    [Test]
    public async Task WhatIsFoldedNamesNoRowAndStillSaysWhatItRead()
    {
        var (processor, store) = Disclosures.Audited();
        var grouped = Orders
            .Where(_ => _.Amount > 0)
            .GroupBy(_ => _.Region)
            .Select(_ => new
            {
                Region = _.Key,
                Total = _.Sum(order => order.Amount),
                Largest = _.Max(order => order.Quantity)
            })
            .ToScryRequest();
        var folded = Orders
            .OrderBy(_ => _.Region)
            .Select(_ => new
            {
                _.Region,
                Lines = _.Lines.Count(),
                Units = _.Lines.Sum(line => line.Units),
                Heavy = _.Lines.Any(line => line.Quantity > 4)
            })
            .ToScryRequest();
        var counted = Employees
            .Where(_ => _.Active)
            .ToScryRequest(new CountOp());
        var distinct = QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Status", new NodeValue(new MemberNode(["Status"])))])),
                new DistinctOp()
            ]);

        await Disclosures.Buffered(processor, grouped);
        var groups = await Disclosures.Shape(store);
        await Disclosures.Buffered(processor, folded);
        var folds = await Disclosures.Shape(store);
        await Disclosures.Buffered(processor, counted);
        var count = await Disclosures.Shape(store);
        await Disclosures.Buffered(processor, distinct);

        await Verify(
            new
            {
                Grouped = groups,
                Folded = folds,
                Counted = count,
                Distinct = await Disclosures.Shape(store)
            });
    }

    // A page reads its ordering keys a second time, and a key the query never named as a tiebreak, to
    // make the next page's cursor. Those are the server's reads, and are no part of what the query is
    // recorded as having read.
    [Test]
    public async Task APagesOwnReadsAreNotTheQuerys()
    {
        var (processor, store) = Disclosures.Audited();
        var request = Employees
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name
            })
            .ToScryRequest(new PageOp(Size: 2));

        await Disclosures.Buffered(processor, request);

        await Verify(await Disclosures.Shape(store));
    }

    // Returning a member the model marks sensitive marks the answer; filtering by one does not, though
    // the record still says it was read.
    [Test]
    public async Task ASensitiveMemberMarksTheAnswerThatReturnsIt()
    {
        var (processor, store) = Disclosures.Audited();
        var returned = Employees
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                _.Avatar
            })
            .ToScryRequest();
        var filtered = Employees
            .Where(_ => _.Workstation!.Value.Extension == "4471")
            .Select(_ => new
            {
                _.Name
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, returned);
        var marked = (await Disclosures.Events(store))[^1];
        await Disclosures.Buffered(processor, filtered);
        var unmarked = (await Disclosures.Events(store))[^1];

        using (Assert.Multiple())
        {
            await Assert.That(marked.Event.Sensitive).IsTrue();
            await Assert.That(unmarked.Event.Sensitive).IsFalse();
            await Assert.That(Fields(await store.Reconstruct(marked.Event.Id))).Contains("Employee.Avatar: Returned, sensitive");
            await Assert.That(Fields(await store.Reconstruct(unmarked.Event.Id))).Contains("Employee.Workstation.Extension: Read, sensitive");
        }
    }

    // The three questions the record exists to answer, asked of what two callers were sent.
    [Test]
    public async Task TheThreeQuestionsAreAnsweredFromTheRecord()
    {
        var (processor, store) = Disclosures.Audited();
        var avatars = Employees
            .Where(_ => _.Name == "Aaron")
            .Select(_ => new
            {
                _.Name,
                _.Avatar
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, Disclosures.Names(), caller: "alice");
        await Disclosures.Buffered(processor, avatars, caller: "bob");
        await Disclosures.Buffered(processor, Employees.Where(_ => _.Active).ToScryRequest(new CountOp()), caller: "carol");

        // Aaron's key, read off the record rather than assumed.
        var listed = (await Disclosures.Events(store, "alice")).Single();
        var aaron = (await store.Reconstruct(listed.Event.Id))!.Units[0].Entities.Single();
        var key = JsonSerializer.Deserialize<int[]>(aaron.Key)!.Cast<object?>().ToList();

        var received = await store.ReceiversOf("Employee", key).ToListAsync();
        using (Assert.Multiple())
        {
            // Who received the row, and which version: two callers, two different contents.
            await Assert.That(received.Select(_ => _.Event.Caller!)).IsEquivalentTo(["bob", "alice"], CollectionOrdering.Matching);
            await Assert.That(received[0].Content).IsNotEqualTo(received[1].Content);

            // What a caller received: the events, and each one's content back out of the store.
            await Assert.That(await Disclosures.Events(store, "bob")).Count().IsEqualTo(1);

            // Whether a caller was ever sent a member: bob was sent an avatar, alice was not, and
            // nobody was sent Active by filtering on it.
            await Assert.That(await store.MemberReceivedBy("bob", "Employee", "Avatar").ToListAsync()).Count().IsEqualTo(1);
            await Assert.That(await store.MemberReceivedBy("alice", "Employee", "Avatar").ToListAsync()).IsEmpty();
            await Assert.That(await store.MemberReceivedBy("alice", "Employee", "Name").ToListAsync()).Count().IsEqualTo(1);
            await Assert.That(await store.MemberReceivedBy("alice", "Employee", "Active").ToListAsync()).IsEmpty();
            await Assert.That(await store.MemberReceivedBy("carol", "Employee", "Active").ToListAsync()).IsEmpty();
        }
    }

    // Asked directly rather than written to a buffer, the answer is shaped into dictionaries on the
    // way out. It is read from the same rows, and the record says so in the same words.
    [Test]
    public async Task AskedDirectlyTheSameRowsAreNamed()
    {
        var (processor, store) = Disclosures.Audited();
        var request = Employees
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                Manager = _.Manager!.Name
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);
        var written = Verifiable(await Disclosures.Shape(store));
        Disclosures.Direct(processor, request);
        var direct = Verifiable(await Disclosures.Shape(store));

        await Assert.That(direct).IsEqualTo(written);
    }

    static string Verifiable(object shape) =>
        JsonSerializer.Serialize(shape);

    static List<string> Fields(ScryDisclosedResponse? answer) =>
        [.. answer!.Shape!.Fields.Select(Disclosures.Field)];

    sealed class EngineeringOnly :
        IReturnablePolicy<Department>
    {
        public IQueryable<Department> Filter(IQueryable<Department> source, ScryPolicyContext context) =>
            source.Where(_ => _.Name == "Engineering");
    }
}
