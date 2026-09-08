/// <summary>
/// A row policy is applied through a typed call rather than a reflective invoke. What has to hold is
/// what the invoke gave: the policy is the request's own — resolved from its services where it is
/// registered there, a fresh instance otherwise — and its failure arrives as it was thrown, which is
/// also how a cached policy's has always arrived.
/// </summary>
[TestFixture]
public class PolicyInvocationTests
{
    [Test]
    public void ARegisteredPolicyIsTheScopesOwn()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Employee, RecordingPolicy>());
        using var provider = new ServiceCollection().AddScoped<RecordingPolicy>().BuildServiceProvider();
        RecordingPolicy.Applied.Clear();

        using (var first = provider.CreateScope())
        {
            processor.Execute(Count(), context, first.ServiceProvider);
            processor.Execute(Count(), context, first.ServiceProvider);
        }

        using (var second = provider.CreateScope())
        {
            processor.Execute(Count(), context, second.ServiceProvider);
        }

        Assert.Multiple(() =>
        {
            Assert.That(RecordingPolicy.Applied, Has.Count.EqualTo(3));
            Assert.That(RecordingPolicy.Applied[1], Is.SameAs(RecordingPolicy.Applied[0]));
            Assert.That(RecordingPolicy.Applied[2], Is.Not.SameAs(RecordingPolicy.Applied[0]));
        });
    }

    // Where the services have nothing, the policy is constructed — per request, since a policy may
    // carry state of its own, and never held across requests.
    [Test]
    public void AnUnregisteredPolicyIsConstructedPerRequest()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Employee, RecordingPolicy>());
        RecordingPolicy.Applied.Clear();

        processor.Execute(Count(), context);
        processor.Execute(Count(), context);

        Assert.Multiple(() =>
        {
            Assert.That(RecordingPolicy.Applied, Has.Count.EqualTo(2));
            Assert.That(RecordingPolicy.Applied[1], Is.Not.SameAs(RecordingPolicy.Applied[0]));
        });
    }

    // A policy's refusal is a rejection, as a cached policy's has always been — not a fault wrapped
    // in the invoke that reached it.
    [Test]
    public void APolicysRefusalArrivesAsItWasThrown()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Build(_ => _.AddPolicy<Employee, RefusingPolicy>());

        var exception = Assert.Throws<ScryValidationException>(() => processor.Execute(Count(), context))!;

        Assert.That(exception.Message, Is.EqualTo("Refused by the policy."));
    }

    static QueryRequest Count() =>
        QueryRequest.Create("Employee", [new CountOp()]);

    static ScryProcessor Build(Action<ScryOptions> extra) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            extra(options);
        });

    public sealed class RecordingPolicy :
        IReturnablePolicy<Employee>
    {
        public static readonly List<RecordingPolicy> Applied = [];

        public IQueryable<Employee> Filter(IQueryable<Employee> source, ScryPolicyContext context)
        {
            Applied.Add(this);
            return source;
        }
    }

    sealed class RefusingPolicy :
        IReturnablePolicy<Employee>
    {
        public IQueryable<Employee> Filter(IQueryable<Employee> source, ScryPolicyContext context) =>
            throw new ScryValidationException("Refused by the policy.");
    }
}
