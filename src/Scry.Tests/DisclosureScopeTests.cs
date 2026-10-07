/// <summary>
/// A source the host left out of the disclosure audit: what reads nothing else is not recorded, and
/// what reads anything else is recorded whole. The rule errs towards the record, so leaving one
/// source out never hides what was sent of another.
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

    // Rooted at the source that was left out, and reading one that was not: recorded, whole, and
    // owed everything a recorded answer is. The excluded side's rows are named with the rest.
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

        await Disclosures.Buffered(processor, request, responseHeaders: headers);

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(headers["Cache-Control"].ToString()).IsEqualTo("no-store");
            await Assert.That(events).Count().IsEqualTo(1);
            var answer = (await store.Reconstruct(events[0].Event.Id))!;
            await Assert.That(answer.Shape!.Fields.Select(_ => _.Source).Distinct().Order()).IsEquivalentTo(["Department", "Employee"]);
            await Assert.That(answer.Units.SelectMany(_ => _.Entities).Select(_ => _.Source).Distinct().Order()).IsEquivalentTo(["Department", "Employee"]);
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
    // always going to be recorded, and what it read of the excluded source is in the record of it.
    [Test]
    public async Task ARecordedSourceReachingAnExcludedOneIsRecordedWhole()
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

        await Disclosures.Buffered(processor, request);

        var events = await Disclosures.Events(store);
        await Assert.That(events).Count().IsEqualTo(1);
        var answer = (await store.Reconstruct(events[0].Event.Id))!;
        await Assert.That(answer.Shape!.Fields.Any(_ => _ is {Source: "Department", Member: "Name"})).IsTrue();
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

        await Assert.That(await Disclosures.Events(store)).Count().IsEqualTo(1);
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
}
