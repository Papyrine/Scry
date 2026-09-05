/// <summary>
/// A policy is built from the request's services, or reflectively where they have nothing — and a
/// policy that neither can build was, until the startup check, a 500 on every query of its source
/// after a startup that passed. The check runs every policy the schema will apply through the same
/// two doors once, before the first request.
/// </summary>
[TestFixture]
public class PolicyResolutionTests
{
    [Test]
    public void APolicyNeitherRegisteredNorConstructibleIsRefusedAtStartup()
    {
        var processor = Build(_ => _.AddPolicy<Order, NeedsAClockPolicy>());
        var services = new ServiceCollection().BuildServiceProvider();

        var exception = Assert.Throws<Exception>(() => processor.EnsurePoliciesResolvable(services))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain("NeedsAClockPolicy"));
            Assert.That(exception.Message, Does.Contain("'Order'"));
            Assert.That(exception.Message, Does.Contain("AddScoped<NeedsAClockPolicy>"));
        });
    }

    [Test]
    public void ARegisteredPolicyPasses()
    {
        var processor = Build(_ => _.AddPolicy<Order, NeedsAClockPolicy>());
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddScoped<NeedsAClockPolicy>()
            .BuildServiceProvider();

        Assert.DoesNotThrow(() => processor.EnsurePoliciesResolvable(services));
    }

    // The default configuration's policies, attachment check included, all have a parameterless
    // constructor, so a host with no registrations at all still starts.
    [Test]
    public void ConstructiblePoliciesPassWithoutRegistration() =>
        Assert.DoesNotThrow(() => SharedProcessor.Instance.EnsurePoliciesResolvable(new ServiceCollection().BuildServiceProvider()));

    static ScryProcessor Build(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });
}
