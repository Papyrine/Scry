/// <summary>
/// A row policy hides the rows it denies, which is the only answer that tells a caller nothing. Where
/// that silence is worse than the disclosure — an internal tool whose users would rather be told than
/// wonder — a policy can be configured to fail the request instead. What must not happen either way is
/// a denied row reaching a result, so the check runs before anything executes.
/// </summary>
[TestFixture]
public class DeniedRowTests
{
    [Test]
    public async Task HidingIsWhatAPolicyDoesUnlessItSaysOtherwise()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, With(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>()));

        // The inactive employee is simply absent, and the query succeeds — the behaviour every policy
        // had before a denial could be configured to do anything else.
        var rows = await client.Source<Employee>("Employee")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Aaron", "Alice", "Carol"]));
    }

    [Test]
    public void AListWhosePolicyErrorsFailsRatherThanQuietlyDroppingARow()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, ErroringOnLists());

        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Employee>("Employee")
                .Select(_ => new {_.Name})
                .ToListAsync());
    }

    [Test]
    public async Task AQueryThatWouldNotHaveReadTheDeniedRowSucceeds()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, ErroringOnLists());

        // The client's own filter excludes the only denied row, so nothing it asked for was withheld
        // and there is nothing to report. A probe that ignored the filter would fail this.
        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Alice"]));
    }

    [Test]
    public void ACountIsAListPositionToo()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, ErroringOnLists());

        // Folding the rows into a number does not make the denial disappear: the count would have been
        // one short, which is exactly what the mode exists to refuse to answer.
        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Employee>("Employee").CountAsync());
    }

    [Test]
    public async Task ThePositionsAnswerSeparately()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, ErroringOnLists());

        // Only RootList was raised, so a single-row terminal still hides — the two are configured
        // apart because a host can reasonably want exactly one of them.
        var row = await client.Source<Employee>("Employee")
            .Where(_ => !_.Active)
            .Select(_ => new {_.Name})
            .FirstOrDefaultAsync();

        Assert.That(row, Is.Null);
    }

    [Test]
    public void ASingleRowTerminalErrorsWhereThatPositionSaysSo()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(
            context,
            With(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>(new()
            {
                RootSingle = DeniedRowMode.Error
            })));

        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Employee>("Employee")
                .Where(_ => !_.Active)
                .Select(_ => new {_.Name})
                .FirstOrDefaultAsync());
    }

    [Test]
    public async Task ARowAnotherPolicyAlreadyHidIsNotOneADenialIsReportedFor()
    {
        await using var context = TestContext.CreateSeeded();

        // Asset's policy hides the trailer; Vehicle's denies it. The trailer was never in the set this
        // caller could see, so the request is not failed for it — an error reports what a caller lost
        // to this policy, not what it was never going to be shown.
        var client = ClientFor(
            context,
            With(_ =>
            {
                _.AddPolicy<Asset, VisibleAssetsOnlyPolicy>();
                _.AddPolicy<Vehicle, FourWheeledVehiclesOnlyPolicy>(Erroring);
            }));

        var rows = await client.Source<Vehicle>("Vehicle")
            .Select(_ => new {_.Name})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.EqualTo(["Van"]));
    }

    [Test]
    public void ARowOnlyTheErroringPolicyDeniesFailsTheRequest()
    {
        using var context = TestContext.CreateSeeded();

        // The other direction over the same pair: the trailer is hidden as before, but the van — which
        // the hiding policy allows — is the one denied, and losing it is what gets reported.
        var client = ClientFor(
            context,
            With(_ =>
            {
                _.AddPolicy<Asset, VisibleAssetsOnlyPolicy>();
                _.AddPolicy<Vehicle, TwoWheeledVehiclesOnlyPolicy>(Erroring);
            }));

        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Vehicle>("Vehicle")
                .Select(_ => new {_.Name})
                .ToListAsync());
    }

    [Test]
    public void NarrowingAppliesTheDerivedTypesModeToo()
    {
        using var context = TestContext.CreateSeeded();

        // Rooted at Asset, where nothing errors, and narrowed to Vehicle, where something does. The
        // policy the narrowing added is the one that answers.
        var client = ClientFor(context, With(_ => _.AddPolicy<Vehicle, TwoWheeledVehiclesOnlyPolicy>(Erroring)));

        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Asset>("Asset")
                .OfType<Vehicle>()
                .Select(_ => new {_.Name})
                .ToListAsync());
    }

    [Test]
    public void ADeniedRowBeyondThePageStillFailsTheRequest()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, ErroringOnLists());

        // Paging picks among the rows that matched, so a denial is reported for the rows the query
        // asked for rather than for the window it happened to read: the answer does not depend on
        // where in the result the denied row fell.
        Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Employee>("Employee")
                .OrderBy(_ => _.Name)
                .Take(1)
                .Select(_ => new {_.Name})
                .ToListAsync());
    }

    [Test]
    public void ABatchEntryIsDeniedWithoutTakingTheBatchWithIt()
    {
        using var context = TestContext.CreateSeeded();
        var batch = new QueryBatchRequest(
            WireFormat.Version,
            [
                QueryRequest.Create("Employee", [new CountOp()]),
                QueryRequest.Create("Ticket", [new CountOp()])
            ]);

        var response = ErroringOnLists().ExecuteBatch(batch, context);

        Assert.That(response.Results[0].Status, Is.EqualTo(403));
        Assert.That(response.Results[0].Error, Is.EqualTo(ScryPermissionException.DeniedMessage));
        // The entry that asked for nothing denied is answered as it would have been on its own.
        Assert.That(response.Results[1].Response, Is.Not.Null);
    }

    [Test]
    public void TheDeniedMessageNamesNothingAboutWhatDeniedIt()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context, ErroringOnLists());

        var exception = Assert.ThrowsAsync<ScryPermissionException>(
            () => client.Source<Employee>("Employee")
                .Select(_ => new {_.Name})
                .ToListAsync());

        // Erroring already discloses that something matched. Naming the source, the row, or the policy
        // would disclose the shape of the policy on top of it.
        Assert.That(exception!.Message, Is.EqualTo(ScryPermissionException.DeniedMessage));
        Assert.That(exception.Message, Does.Not.Contain("Employee"));
        Assert.That(exception.Message, Does.Not.Contain("Active"));
    }

    [Test]
    public void ShowingTheSqlRunsNothingAndSoDeniesNothing()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]);

        // A preview executes no query, so there is no read for a policy to have denied — and asking
        // for one must not become a way to run the probe's queries either.
        var sql = ErroringOnLists().ToQueryString(request, context, EmptyServiceProvider.Instance);

        Assert.That(sql, Does.Contain("SELECT"));
    }

    static readonly DeniedRowHandling Erroring = new()
    {
        RootList = DeniedRowMode.Error
    };

    // ActiveOnlyPolicy denies the one inactive employee, so every query rooted at Employee that does
    // not filter it out has exactly one denied row to report.
    static ScryProcessor ErroringOnLists() =>
        With(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>(Erroring));

    static ScryProcessor With(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });

    static ScryClient ClientFor(TestContext context, ScryProcessor processor) =>
        new((request, _) => Task.FromResult(processor.Execute(request, context)));
}
