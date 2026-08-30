/// <summary>
/// A row policy filters a source, and a navigation into that source is a second way to reach its
/// rows. Every rooted member path is rebound through one place, so the policy applies wherever the
/// traversal appears — a projection leaf, a predicate, an ordering, a key — and a row the policy hides
/// reads as null rather than being handed over or answered about.
/// </summary>
[TestFixture]
public class NavigationPolicyTests
{
    /// <summary>Hides Sales, so an employee of it navigates into a row the policy does not return.</summary>
    // begin-snippet: navigationPolicy
    class EngineeringOnlyPolicy :
        IReturnablePolicy<Department>
    {
        public IQueryable<Department> Filter(IQueryable<Department> source, ScryPolicyContext context) =>
            source.Where(_ => _.Name == "Engineering");
    }
    // end-snippet

    [Test]
    public async Task ProjectionLeafReadsNullForAHiddenTarget()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // begin-snippet: navigationPolicyQuery
        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name, Department = (string?)_.Department!.Name})
            .ToListAsync();
        // end-snippet

        // Bob and Carol are in Sales, which the policy hides: the employee row is still returned, and
        // the department it names reads as absent.
        Assert.That(
            rows.Select(_ => $"{_.Name}:{_.Department ?? "<null>"}"),
            Is.EqualTo(["Aaron:Engineering", "Alice:Engineering", "Bob:<null>", "Carol:<null>"]));
    }

    [Test]
    public async Task PredicateCannotAskAboutAHiddenTarget()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // The oracle this closes: the predicate runs in SQL, so without the policy applied at the
        // traversal it would answer about rows a direct query of Department could never return.
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Department!.Name == "Sales")
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public async Task ValueTypedLeafWidensRatherThanFaulting()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A non-nullable value read through a policied traversal has to be able to carry the null the
        // policy produces, or the shaper faults materializing it.
        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name, Department = (int?)_.Department!.Id})
            .ToListAsync();

        Assert.That(
            rows.Select(_ => _.Department is null),
            Is.EqualTo([false, false, true, true]));
    }

    [Test]
    public async Task OrderingSortsHiddenTargetsAsAbsent()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Department!.Name)
            .ThenBy(_ => _.Name)
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Bob", "Carol", "Aaron", "Alice"]));
    }

    [Test]
    public async Task GroupKeyGroupsHiddenTargetsTogether()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .GroupBy(_ => _.Department!.Name)
            .Select(_ => new {Department = (string?)_.Key, Count = _.Count()})
            .ToListAsync();

        Assert.That(
            rows.Select(_ => $"{_.Department ?? "<null>"}:{_.Count}").Order(),
            Is.EqualTo(["<null>:2", "Engineering:2"]));
    }

    [Test]
    public async Task NestedProjectionReadsNullForAHiddenTarget()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name, Department = new {Name = (string?)_.Department!.Name}})
            .ToListAsync();

        Assert.That(
            rows.Select(_ => _.Department.Name ?? "<null>"),
            Is.EqualTo(["Engineering", "Engineering", "<null>", "<null>"]));
    }

    [Test]
    public async Task UnpolicedNavigationIsUnaffected()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // Manager navigates into Employee, which carries no policy here: the traversal is left alone.
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Manager!.Name == "Alice")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Aaron", "Bob"]));
    }

    [Test]
    public async Task JoinProjectionReadsNullForAHiddenTarget()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        // A join resolves its own sides through their policies. This is the other traversal: a member
        // path on a joined side that steps into a policied source. Left so every outer row survives
        // and the traversal is what nulls, rather than the join.
        var rows = await client.Source<Employee>("Employee")
            .LeftJoin(
                client.Source<Department>("Department"),
                _ => _.DepartmentId,
                _ => _.Id,
                (employee, department) => new {Employee = employee.Name, Traversed = employee.Department!.Name, Joined = department!.Name})
            .ToListAsync();

        // A join projection member is a bare member path, so the (string?) widening the other
        // projections use has no room here; the members carry the model's non-null annotation while
        // the policy nulls them anyway, and the display helper is where that reality is admitted.
        Assert.That(
            rows.Select(_ => $"{_.Employee}:{Display(_.Traversed)}:{Display(_.Joined)}").Order(),
            Is.EqualTo(["Aaron:Engineering:Engineering", "Alice:Engineering:Engineering", "Bob:<null>:<null>", "Carol:<null>:<null>"]));
    }

    [Test]
    public async Task AggregateSelectorReadsThroughThePolicy()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, OrderPolicied());

        // The aggregate's selector is a member path rooted at the collection element, so it reaches a
        // policied source the same way any other path does — through the policy.
        var rows = await client.Source<Order>("Order")
            .OrderBy(_ => _.Region)
            .Select(_ => new
            {
                _.Region,
                Lines = _.Lines.Count,
                Total = _.Lines.Sum(_ => _.Order!.Amount)
            })
            .ToListAsync();

        // Only North survives Order's own root policy, and its lines navigate back to a visible order,
        // so the sum is the amount rather than the null a hidden one would have produced.
        Assert.That(
            rows.Select(_ => $"{_.Region}:{_.Lines}:{_.Total}").ToArray(),
            Is.EqualTo(["North:2:200.00", "North:1:250.00"]));
    }

    [Test]
    public async Task ChainedPoliciedNavigationsApplyBothPolicies()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, BothPolicied());

        // Manager steps into policied Employee and Department steps into policied Department, so the
        // two nest. Bob is hidden by Employee's own root policy; Aaron's manager Alice is active and in
        // Engineering, so both steps resolve; Alice and Carol have no manager at all.
        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name, Manager = (string?)_.Manager!.Name, ManagerDepartment = (string?)_.Manager!.Department!.Name})
            .ToListAsync();

        Assert.That(
            rows.Select(_ => $"{_.Name}:{_.Manager ?? "<null>"}:{_.ManagerDepartment ?? "<null>"}").ToArray(),
            Is.EqualTo(["Aaron:Alice:Engineering", "Alice:<null>:<null>", "Carol:<null>:<null>"]));
    }

    /// <summary>Hides the inactive employee, so a traversal into Employee has rows to hide too.</summary>
    class ActiveEmployeesOnlyPolicy :
        IReturnablePolicy<Employee>
    {
        public IQueryable<Employee> Filter(IQueryable<Employee> source, ScryPolicyContext context) =>
            source.Where(_ => _.Active);
    }

    /// <summary>Hides every order but North's.</summary>
    class NorthOrdersOnlyPolicy :
        IReturnablePolicy<Order>
    {
        public IQueryable<Order> Filter(IQueryable<Order> source, ScryPolicyContext context) =>
            source.Where(_ => _.Region == "North");
    }

    static ScryProcessor OrderPolicied() =>
        Build(_ => _.AddPolicy<Order, NorthOrdersOnlyPolicy>());

    static ScryProcessor BothPolicied() =>
        Build(options =>
        {
            options.AddPolicy<Department, EngineeringOnlyPolicy>();
            options.AddPolicy<Employee, ActiveEmployeesOnlyPolicy>();
        });

    [Test]
    public async Task ProbeAcceptsAPolicyThatComposes()
    {
        await using var context = TestContext.CreateSeeded();

        // Employee.Department navigates into policied Department, and the policy is a plain filter, so
        // it translates where it is applied.
        Assert.DoesNotThrow(() => Processor().ProbePoliciedNavigations(context));
    }

    [Test]
    public async Task ProbeRejectsAPolicyThatDoesNotTranslate()
    {
        await using var context = TestContext.CreateSeeded();

        // The failure the probe exists for. A policy whose predicate the provider cannot translate is
        // caught here, naming the policy and the navigation that reaches it, rather than as a generic
        // 500 on the first client to name the member.
        var exception = Assert.Throws<Exception>(
            () => Build(_ => _.AddPolicy<Department, UntranslatablePolicy>()).ProbePoliciedNavigations(context));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("'Department'"));
            Assert.That(exception.Message, Does.Contain("Employee.Department"));
            Assert.That(exception.Message, Does.Contain("correlated subquery"));
        });
    }

    [Test]
    public async Task ProbeIsSkippedWhereTheHostClearsIt()
    {
        await using var context = TestContext.CreateSeeded();

        // A policy that cannot answer outside a request opts out of the startup proof. Nothing about
        // how the policy applies per request changes, so the traversal is still filtered.
        var processor = Build(options =>
        {
            options.ProbePoliciedNavigations = false;
            options.AddPolicy<Department, EngineeringOnlyPolicy>();
        });

        var client = ClientFor(context, processor);
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Department!.Name == "Sales")
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows, Is.Empty);
    }

    [Test]
    public void ATraversalIntoADeniedRowFailsWhereThePolicySaysSo()
    {
        using var context = TestContext.CreateSeeded();

        // Bob and Carol are in Sales, which the policy hides. Reading the department reads nothing for
        // them, and this policy would rather say so than answer with a null.
        var client = ClientFor(context, Erroring());

        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Employee>("Employee")
                .Select(_ => new {_.Name, Department = _.Department!.Name})
                .ToListAsync());
    }

    [Test]
    public void APredicateOverTheTraversalIsTheSameRead()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Erroring());

        // Nothing is projected, but the predicate still runs over rows the policy hides — the oracle
        // the traversal rewrite closes, and the same read as far as a denial is concerned.
        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Employee>("Employee")
                .Where(_ => _.Department!.Name == "Sales")
                .Select(_ => new {_.Name})
                .ToListAsync());
    }

    [Test]
    public void AQueryThatNeverStepsIntoTheSourceIsUnaffected()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, Erroring());

        // The denial is about the traversal. A query that does not name it reads no policied row and
        // has nothing to be told about.
        Assert.DoesNotThrowAsync(
            () => client.Source<Employee>("Employee")
                .Select(_ => new {_.Name})
                .ToListAsync());
    }

    [Test]
    public void ShowingTheSqlStepsIntoNothing()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Department", new NodeValue(new MemberNode(["Department", "Name"])))]))]);

        // A preview runs no query, so the traversal has read nothing for a policy to have denied.
        var sql = Erroring().ToQueryString(request, context, EmptyServiceProvider.Instance);

        Assert.That(sql, Does.Contain("SELECT"));
    }

    static ScryProcessor Erroring() =>
        Build(_ => _.AddPolicy<Department, EngineeringOnlyPolicy>(new()
        {
            Navigation = DeniedRowMode.Error
        }));

    /// <summary>
    /// Filters on something the provider cannot translate. Untranslatable at the root too — what the
    /// probe is proving is that a policy reaches the provider from where a traversal applies it.
    /// </summary>
    class UntranslatablePolicy :
        IReturnablePolicy<Department>
    {
        public IQueryable<Department> Filter(IQueryable<Department> source, ScryPolicyContext context) =>
            source.Where(_ => Allowed(_.Name));

        static bool Allowed(string name) => name.Length > 0;
    }

    static ScryProcessor Build(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AddPolicy<Department, EngineeringOnlyPolicy>();
        });

    static string Display(string? value) =>
        value ?? "<null>";

    static ScryClient ClientFor(TestContext context) =>
        ClientFor(context, Processor());

    static ScryClient ClientFor(TestContext context, ScryProcessor processor) =>
        new((request, _) => Task.FromResult(processor.Execute(request, context)));
}
