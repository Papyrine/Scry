/// <summary>
/// A policy is built from the request's services, or reflectively where they have nothing — and a
/// policy that neither can build was, until the startup check, a 500 on every query of its source
/// after a startup that passed. The check runs every policy the schema will apply through the same
/// two doors once, before the first request.
/// </summary>
public class PolicyResolutionTests
{
    [Test]
    public async Task APolicyNeitherRegisteredNorConstructibleIsRefusedAtStartup()
    {
        var processor = Build(_ => _.AddPolicy<Order, NeedsAClockPolicy>());
        var services = new ServiceCollection().BuildServiceProvider();

        var exception = Assert.ThrowsExactly<Exception>(() => processor.EnsurePoliciesResolvable(services));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("NeedsAClockPolicy");
            await Assert.That(exception.Message).Contains("'Order'");
            await Assert.That(exception.Message).Contains("AddScoped<NeedsAClockPolicy>");
        }
    }

    // The startup probe runs every policy a traversal reaches once, with empty headers and no
    // principal. One that cannot answer under those conditions fails startup naming itself, which is
    // the moment to either give it a default or clear ProbePoliciedNavigations.
    [Test]
    public async Task APolicyThatThrowsUnderTheStartupProbeFailsStartupNamingIt()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Department, NeedsAPrincipalPolicy>());

        var exception = Assert.ThrowsExactly<Exception>(() => processor.ProbePoliciedNavigations(context, new ServiceCollection().BuildServiceProvider()));

        await Assert.That(exception.ToString()).Contains("NeedsAPrincipalPolicy");
    }

    [Test]
    public async Task ARegisteredPolicyPasses()
    {
        var processor = Build(_ => _.AddPolicy<Order, NeedsAClockPolicy>());
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddScoped<NeedsAClockPolicy>()
            .BuildServiceProvider();

        await Assert.That(() => processor.EnsurePoliciesResolvable(services)).ThrowsNothing();
    }

    // The default configuration's policies, attachment check included, all have a parameterless
    // constructor, so a host with no registrations at all still starts.
    [Test]
    public async Task ConstructiblePoliciesPassWithoutRegistration() =>
        await Assert.That(() => SharedProcessor.Instance.EnsurePoliciesResolvable(new ServiceCollection().BuildServiceProvider())).ThrowsNothing();

    static ScryProcessor Build(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });
}

/// <summary>Never attached by default. Cannot answer without a principal, which the startup probe has none of.</summary>
public sealed class NeedsAPrincipalPolicy :
    IReturnablePolicy<Department>
{
    public IQueryable<Department> Filter(IQueryable<Department> source, ScryPolicyContext context) =>
        throw new InvalidOperationException("No principal to scope by.");
}
