/// <summary>
/// A source or a member the host left out of the disclosure audit: what reads nothing else is not
/// recorded, and what reads anything else is recorded, with what was read from what was left out
/// marked in its place. The rule errs towards the record, so leaving one out never hides what was
/// sent of another.
/// </summary>
public class DisclosureScopeTests
{
    static IQueryable<Employee> Employees => Disclosures.From<Employee>("Employee");

    static IQueryable<Department> Departments => Disclosures.From<Department>("Department");

    static QueryRequest DepartmentNames() =>
        Departments
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name
            })
            .ToScryRequest();

    // The excluded source on its own leaves nothing in the record, and the response is not marked for
    // the audit's sake: nothing about it is the audit's concern. The source beside it is recorded as
    // it always was.
    [Test]
    public async Task AnAnswerOfNothingButAnExcludedSourceIsNotRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>());
        var plain = new HeaderDictionary();
        var recorded = new HeaderDictionary();

        var sent = await Disclosures.Buffered(processor, DepartmentNames(), responseHeaders: plain);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, DepartmentNames());
        Disclosures.Direct(processor, DepartmentNames());
        await Disclosures.Buffered(processor, Disclosures.Names(), responseHeaders: recorded);

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(plain["Cache-Control"].ToString()).IsNotEqualTo("no-store");
            await Assert.That(recorded["Cache-Control"].ToString()).IsEqualTo("no-store");
            await Assert.That(events).Count().IsEqualTo(1);
            await Assert.That(events[0].Event.Source).IsEqualTo("Employee");
        }
    }

    // Nobody has to be named for an answer nothing is recorded of. One that is recorded still does.
    [Test]
    public async Task AnAnswerThatIsNotRecordedNeedsNobodyToRecordItUnder()
    {
        var (processor, store) = Disclosures.Audited(
            configure: _ =>
            {
                _.Caller = _ => null;
                _.Exclude<Department>();
            });

        await Disclosures.Buffered(processor, DepartmentNames());
        var refused = await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            () => Disclosures.Buffered(processor, Disclosures.Names()));

        using (Assert.Multiple())
        {
            await Assert.That(refused!.Message).Contains("has no caller");
            await Assert.That((await store.Status()).Events).IsEqualTo(0);
        }
    }

    // Rooted at the source that was left out, and reading one that was not: recorded, and owed
    // everything a recorded answer is. The excluded side's rows are named with the rest, and a
    // marker stands where their values were.
    [Test]
    public async Task AnExcludedSourceReadBesideAnotherIsRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>());
        var headers = new HeaderDictionary();
        var request = Departments
            .OrderBy(_ => _.Name)
            .Join(
                Employees,
                _ => _.Id,
                _ => _.DepartmentId,
                (department, employee) => new
                {
                    Department = department.Name,
                    Employee = employee.Name
                })
            .ToScryRequest();

        var sent = await Disclosures.Buffered(processor, request, responseHeaders: headers);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, request);

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(headers["Cache-Control"].ToString()).IsEqualTo("no-store");
            await Assert.That(events).Count().IsEqualTo(1);
            var answer = (await store.Reconstruct(events[0].Event.Id))!;
            await Assert.That(answer.Shape!.Fields.Select(_ => _.Source).Distinct().Order()).IsEquivalentTo(["Department", "Employee"]);
            await Assert.That(answer.Units.SelectMany(_ => _.Entities).Select(_ => _.Source).Distinct().Order()).IsEquivalentTo(["Department", "Employee"]);
            await Assert.That(answer.Units.All(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span).StartsWith("{\"department\":{\"$excluded\":true},\"employee\":\""))).IsTrue();
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
        }
    }

    // The same answer with nobody to record it under is refused, as any recorded answer is: being
    // rooted at an excluded source is no way around being named.
    [Test]
    public async Task AnExcludedRootIsNoWayAroundBeingNamed()
    {
        var (processor, store) = Disclosures.Audited(
            configure: _ =>
            {
                _.Caller = _ => null;
                _.Exclude<Department>();
            });
        var request = Departments
            .Join(
                Employees,
                _ => _.Id,
                _ => _.DepartmentId,
                (department, employee) => new
                {
                    Department = department.Name,
                    Employee = employee.Name
                })
            .ToScryRequest();

        await Assert.ThrowsExactlyAsync<ScryDisclosureException>(() => Disclosures.Buffered(processor, request));

        await Assert.That((await store.Status()).Events).IsEqualTo(0);
    }

    // The other way about: a recorded source reaching the excluded one through a navigation. It was
    // always going to be recorded. That it read the excluded source, and which of its rows, is in
    // the record of it, and what it read of them is not.
    [Test]
    public async Task ARecordedSourceReachingAnExcludedOneIsRecordedWithoutItsValues()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>());
        var request = Employees
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name,
                Department = _.Department!.Name
            })
            .ToScryRequest();

        var sent = await Disclosures.Buffered(processor, request);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, request);

        var events = await Disclosures.Events(store);
        await Assert.That(events).Count().IsEqualTo(1);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        var units = answer.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).ToList();
        using (Assert.Multiple())
        {
            await Assert.That(answer.Shape!.Fields.Any(_ => _ is {Source: "Department", Member: "Name", Use: ScryDisclosureFieldUse.Returned})).IsTrue();
            await Assert.That(answer.Units.All(_ => _.Entities.Any(_ => _.Source == "Department"))).IsTrue();
            await Assert.That(units.All(_ => _.StartsWith("{\"name\":\"") && _.EndsWith(",\"department\":{\"$excluded\":true}}"))).IsTrue();
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
        }
    }

    // A value folded from an excluded source's rows is what they held, and is left out as they are.
    // The answer is still recorded, since the rows were reached from ones that are.
    [Test]
    public async Task AValueFoldedFromAnExcludedSourceIsLeftOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>());
        var largest = Employees.Where(_ => _.Active).ToScryRequest() with
        {
            Pipeline =
            [
                .. Employees.Where(_ => _.Active).ToScryRequest().Pipeline,
                new AggregateOp(AggregateFn.Max, new MemberNode(["Department", "Name"]))
            ]
        };

        var sent = await Disclosures.Buffered(processor, largest);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, largest);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(events[0].Event.Kind).IsEqualTo(ScryDisclosureKind.Scalar);
            await Assert.That(Encoding.UTF8.GetString(answer.Units[0].Content.Bytes.Span)).IsEqualTo("{\"$excluded\":true}");
            await Assert.That(answer.Units[0].Content.Kind).IsEqualTo(ScryDisclosureContentKind.LeftOut);
        }
    }

    // Rows filtered by what an excluded source holds, and counted: the count is no value of the
    // excluded source, and is recorded as it was sent.
    [Test]
    public async Task ACountFilteredByAnExcludedSourceIsRecordedAsSent()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>());
        var request = Employees.Where(_ => _.Department!.Name == "Engineering").ToScryRequest() with
        {
            Pipeline =
            [
                .. Employees.Where(_ => _.Department!.Name == "Engineering").ToScryRequest().Pipeline,
                new CountOp()
            ]
        };

        await Disclosures.Buffered(processor, request);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        var count = Encoding.UTF8.GetString(answer.Units[0].Content.Bytes.Span);
        await Assert.That(int.TryParse(count, CultureInfo.InvariantCulture, out var counted) && counted > 0).IsTrue();
    }

    // A filter is a read. An excluded source narrowed by what a recorded one holds says something
    // about the recorded one, whatever it projects.
    [Test]
    public async Task AnExcludedSourceFilteredByAnotherIsRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>());
        var request = Employees
            .Where(_ => _.Department!.Name == "Engineering")
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);
        await Disclosures.Buffered(processor, Disclosures.Names());

        var events = await Disclosures.Events(store);
        await Assert.That(events).Count().IsEqualTo(1);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(answer.Units).IsNotEmpty();
            await Assert.That(answer.Units.All(_ => _.Entities.Any(_ => _.Source == "Employee"))).IsTrue();
            await Assert.That(answer.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).Distinct()).IsEquivalentTo(["{\"name\":{\"$excluded\":true}}"]);
        }
    }

    // A stream of an excluded source is sent as it would be with the audit off, and leaves nothing.
    [Test]
    public async Task AStreamOfAnExcludedSourceIsNotRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>());
        await using var context = TestContext.CreateSeeded();

        var (_, _, rows) = processor.StreamBuffered(DepartmentNames(), context, EmptyServiceProvider.Instance, new HeaderDictionary(), new HeaderDictionary());
        var count = 0;
        await foreach (var _ in rows)
        {
            count++;
        }

        using (Assert.Multiple())
        {
            await Assert.That(count).IsGreaterThan(0);
            await Assert.That((await store.Status()).Events).IsEqualTo(0);
        }
    }

    static QueryRequest EmployeeNames() =>
        Employees
            .OrderBy(_ => _.Name)
            .Select(_ => new
            {
                _.Name
            })
            .ToScryRequest();

    // A member left out, by the rule a source is left out by: an answer that reads nothing else of
    // the row is not recorded, and is sent as it would be with the audit off.
    [Test]
    public async Task AnAnswerOfNothingButAnExcludedMemberIsNotRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Name));
        var headers = new HeaderDictionary();

        var sent = await Disclosures.Buffered(processor, EmployeeNames(), responseHeaders: headers);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, EmployeeNames());
        Disclosures.Direct(processor, EmployeeNames());

        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(headers["Cache-Control"].ToString()).IsNotEqualTo("no-store");
            await Assert.That((await store.Status()).Events).IsEqualTo(0);
        }
    }

    // One member of the row more, read only to filter by, and the answer is recorded: the excluded
    // member is named among what it returned, and a marker stands where its values were. What was
    // sent is what it would have been with the audit off.
    [Test]
    public async Task AnExcludedMemberReadBesideAnotherIsRecordedWithoutItsValues()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Name));
        var headers = new HeaderDictionary();

        var sent = await Disclosures.Buffered(processor, Disclosures.Names(), responseHeaders: headers);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, Disclosures.Names());

        var events = await Disclosures.Events(store);
        await Assert.That(events).Count().IsEqualTo(1);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(headers["Cache-Control"].ToString()).IsEqualTo("no-store");
            await Assert.That(answer.Shape!.Fields.Any(_ => _ is {Source: "Employee", Member: "Name", Use: ScryDisclosureFieldUse.Returned})).IsTrue();
            await Assert.That(answer.Shape.Fields.Any(_ => _ is {Source: "Employee", Member: "Active", Use: ScryDisclosureFieldUse.Read})).IsTrue();
            await Assert.That(answer.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).Distinct()).IsEquivalentTo(["{\"name\":{\"$excluded\":true}}"]);
            await Assert.That(answer.Units.All(_ => _.Content.Kind == ScryDisclosureContentKind.LeftOut)).IsTrue();
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
        }
    }

    // A row with a member left out and one kept: the kept one is recorded as it was sent, and the
    // excluded one by a marker. So is a value computed from the excluded member, which holds what
    // the member held.
    [Test]
    public async Task OnlyWhatWasReadFromAnExcludedMemberIsLeftOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Name));
        var request = Employees
            .Where(_ => _.Active)
            .OrderBy(_ => _.Id)
            .Select(_ => new
            {
                _.Id,
                _.Name,
                Shouted = _.Name.ToUpper(),
                _.Status
            })
            .ToScryRequest();

        var sent = await Disclosures.Buffered(processor, request);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, request);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        var first = Encoding.UTF8.GetString(answer.Units[0].Content.Bytes.Span);
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(first).StartsWith("{\"id\":");
            await Assert.That(answer.Units.All(_ => _.Content.Kind == ScryDisclosureContentKind.Row)).IsTrue();
            await Assert.That(first).Contains("\"name\":{\"$excluded\":true},\"shouted\":{\"$excluded\":true},\"status\":\"");
            await Assert.That(answer.Units.All(_ => !Encoding.UTF8.GetString(_.Content.Bytes.Span).Contains("\"name\":\""))).IsTrue();
        }
    }

    // A value folded from an excluded member is what the member held, and is left out as it is. A
    // count is not: it reads the rows, and no member's value is in it.
    [Test]
    public async Task AValueFoldedFromAnExcludedMemberIsLeftOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Name));
        var largest = Employees.Where(_ => _.Active).ToScryRequest() with
        {
            Pipeline =
            [
                .. Employees.Where(_ => _.Active).ToScryRequest().Pipeline,
                new AggregateOp(AggregateFn.Max, new MemberNode(["Name"]))
            ]
        };

        var sent = await Disclosures.Buffered(processor, largest);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, largest);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(events[0].Event.Kind).IsEqualTo(ScryDisclosureKind.Scalar);
            await Assert.That(Encoding.UTF8.GetString(answer.Units[0].Content.Bytes.Span)).IsEqualTo("{\"$excluded\":true}");
            await Assert.That(answer.Units[0].Content.Kind).IsEqualTo(ScryDisclosureContentKind.LeftOut);
        }
    }

    // A group key made from an excluded member holds what the member held, though the projection
    // that returns it names only the key. What was counted beside it is recorded as it was sent.
    [Test]
    public async Task AKeyGroupedByAnExcludedMemberIsLeftOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Name));
        var request = Employees
            .GroupBy(_ => _.Name)
            .Select(_ => new
            {
                Name = _.Key,
                Active = _.Count(employee => employee.Active)
            })
            .ToScryRequest();

        var sent = await Disclosures.Buffered(processor, request);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, request);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(answer.Units).IsNotEmpty();
            await Assert.That(answer.Units.All(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span).StartsWith("{\"name\":{\"$excluded\":true},\"active\":"))).IsTrue();
        }
    }

    // The elements of an excluded collection are part of it, so rows flattened out of one are left
    // out as it is, though what is read of them names only the element.
    [Test]
    public async Task RowsFlattenedFromAnExcludedCollectionAreLeftOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.PreviousAddresses));
        var request = Employees
            .Where(_ => _.Active)
            .SelectMany(_ => _.PreviousAddresses)
            .Select(_ => new
            {
                _.City
            })
            .ToScryRequest();

        var sent = await Disclosures.Buffered(processor, request);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, request);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(answer.Units).IsNotEmpty();
            await Assert.That(answer.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).Distinct()).IsEquivalentTo(["{\"city\":{\"$excluded\":true}}"]);
        }
    }

    // Reading nothing of them but the excluded collection's elements reads nothing that is recorded.
    [Test]
    public async Task RowsOfNothingButAnExcludedCollectionAreNotRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.PreviousAddresses));
        var request = Employees
            .SelectMany(_ => _.PreviousAddresses)
            .Select(_ => new
            {
                _.City
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);

        await Assert.That((await store.Status()).Events).IsEqualTo(0);
    }

    // A row of a set operation may come from either side, so a value is left out where the other
    // side read it from an excluded source, whatever the first side read.
    [Test]
    public async Task ASetOperationLeavesOutWhatEitherSideLeavesOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>());
        var request = Employees
            .Select(_ => new
            {
                _.Name
            })
            .Union(
                Departments.Select(_ => new
                {
                    _.Name
                }))
            .ToScryRequest();

        var sent = await Disclosures.Buffered(processor, request);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, request);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(answer.Units).IsNotEmpty();
            await Assert.That(answer.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).Distinct()).IsEquivalentTo(["{\"name\":{\"$excluded\":true}}"]);
        }
    }

    // A stream's rows are recorded as a list's are: written twice where a member is left out, and the
    // lines sent are the lines the audit would not have touched.
    [Test]
    public async Task AStreamLeavesOutWhatAListLeavesOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Name));
        await using var context = TestContext.CreateSeeded();

        var lines = await Streamed(processor, context);
        var unaudited = await Streamed(SharedProcessor.Instance, context);

        var events = await Disclosures.Events(store);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(lines).IsEquivalentTo(unaudited, CollectionOrdering.Matching);
            await Assert.That(answer.Units).Count().IsEqualTo(lines.Count);
            await Assert.That(answer.Units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).Distinct()).IsEquivalentTo(["{\"name\":{\"$excluded\":true}}"]);
        }
    }

    static async Task<List<string>> Streamed(ScryProcessor processor, TestContext context)
    {
        var (_, _, rows) = processor.StreamBuffered(Disclosures.Names(), context, EmptyServiceProvider.Instance, new HeaderDictionary(), new HeaderDictionary());
        List<string> lines = [];
        await foreach (var row in rows)
        {
            lines.Add(Encoding.UTF8.GetString(row.Span));
        }

        return lines;
    }

    // A member of a row's key, and the member named as a row's version, are kept beside every row
    // the record names, so neither can be left out of it: a host asking for both is told so when the
    // server starts.
    [Test]
    public async Task AKeyOrAVersionCannotBeLeftOut()
    {
        await using var context = TestContext.CreateSeeded();
        var (keyed, _) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Id));
        var (versioned, _) = Disclosures.Audited(
            configure: _ =>
            {
                _.Version<Employee>(_ => _.Status);
                _.Exclude<Employee>(_ => _.Status);
            });

        var key = Assert.Throws<Exception>(() => keyed.ValidateAgainstModel(context));
        var version = Assert.Throws<Exception>(() => versioned.ValidateAgainstModel(context));

        using (Assert.Multiple())
        {
            await Assert.That(key.Message).Contains("'Employee.Id' out of the record, and it is part of the key");
            await Assert.That(version.Message).Contains("'Employee.Status' out of the record, and it is the member named as a row's version");
        }
    }

    // A count reads the rows and no member of them. Leaving a member out says nothing of the rows,
    // so it is recorded; counted through the excluded member, it is that member that was read.
    [Test]
    public async Task ACountOfTheRowsIsRecordedWhateverMembersAreLeftOut()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Name));

        await Disclosures.Buffered(processor, QueryRequest.Create("Employee", [new CountOp()]));
        await Disclosures.Buffered(processor, Employees.Where(_ => _.Name != "").ToScryRequest() with
        {
            Pipeline =
            [
                .. Employees.Where(_ => _.Name != "").ToScryRequest().Pipeline,
                new CountOp()
            ]
        });

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(events).Count().IsEqualTo(1);
            await Assert.That(events[0].Event.Kind).IsEqualTo(ScryDisclosureKind.Scalar);
        }
    }

    // Reached through a navigation, the excluded member is still the only thing read of its own
    // row, but the navigation is a member of the row it was reached from, and that one is recorded.
    [Test]
    public async Task AnExcludedMemberReachedFromAnotherRowIsRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Department>(_ => _.Name));
        var request = Employees
            .OrderBy(_ => _.Id)
            .Select(_ => new
            {
                Department = _.Department!.Name
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, request);
        await Disclosures.Buffered(processor, DepartmentNames());

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(events).Count().IsEqualTo(1);
            await Assert.That(events[0].Event.Source).IsEqualTo("Employee");
        }
    }

    // A navigation left out takes what is read through it, as a member that holds a value of several
    // parts does: an answer of nothing else is not recorded, and one that reads anything else is
    // recorded with a marker where each value read through it was. The row it reached is still named
    // by its key.
    [Test]
    public async Task AnExcludedNavigationTakesWhatIsReadThroughIt()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Department));
        var through = Employees
            .OrderBy(_ => _.Department!.Name)
            .Select(_ => new
            {
                Department = _.Department!.Name
            })
            .ToScryRequest();
        var beside = Employees
            .OrderBy(_ => _.Id)
            .Select(_ => new
            {
                _.Id,
                Department = _.Department!.Name
            })
            .ToScryRequest();

        await Disclosures.Buffered(processor, through);
        var sent = await Disclosures.Buffered(processor, beside);
        var unaudited = await Disclosures.Buffered(SharedProcessor.Instance, beside);

        var events = await Disclosures.Events(store);
        await Assert.That(events).Count().IsEqualTo(1);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(sent.AsSpan().SequenceEqual(unaudited)).IsTrue();
            await Assert.That(answer.Units.All(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span).EndsWith(",\"department\":{\"$excluded\":true}}"))).IsTrue();
            await Assert.That(answer.Units.All(_ => _.Entities.Any(_ => _ is {Source: "Department", Via: "Department"}))).IsTrue();
        }
    }

    // Nobody has to be named for an answer of nothing but excluded members, and somebody does for
    // any other: which it is, is known once the query has been read.
    [Test]
    public async Task WhoIsAskingMattersOnlyWhereTheAnswerIsRecorded()
    {
        var (processor, store) = Disclosures.Audited(
            configure: _ =>
            {
                _.Caller = _ => null;
                _.Exclude<Employee>(_ => _.Name);
            });

        await Disclosures.Buffered(processor, EmployeeNames());
        var refused = await Assert.ThrowsExactlyAsync<ScryDisclosureException>(
            () => Disclosures.Buffered(processor, Disclosures.Names()));

        using (Assert.Multiple())
        {
            await Assert.That(refused!.Message).Contains("has no caller");
            await Assert.That((await store.Status()).Events).IsEqualTo(0);
        }
    }

    // An attachment is one member of one row, so one that was left out is fetched unrecorded.
    [Test]
    public async Task AnAttachmentThatIsAnExcludedMemberIsNotRecorded()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.Exclude<Contract>(_ => _.Document));
        await using var context = TestContext.CreateSeeded();

        var found = processor.FetchAttachment(
            AttachmentRequest.Create("Contract", "Document", [new("1", ClrTypeTag.Int32)]),
            context);

        using (Assert.Multiple())
        {
            await Assert.That(found.Value).IsNotNull();
            await Assert.That((await store.Status()).Events).IsEqualTo(0);
        }
    }

    // What is left out is named as a property of the row, and at least one is.
    [Test]
    public async Task AMemberLeftOutIsAPropertyOfTheRow()
    {
        var nested = Assert.ThrowsExactly<ArgumentException>(
            () => Disclosures.Audited(configure: _ => _.Exclude<Employee>(_ => _.Department!.Name)));
        var none = Assert.ThrowsExactly<ArgumentException>(
            () => Disclosures.Audited(configure: _ => _.Exclude<Employee>([])));

        using (Assert.Multiple())
        {
            await Assert.That(nested.Message).Contains("read straight off the row");
            await Assert.That(none.Message).Contains("names at least one");
        }
    }
}
