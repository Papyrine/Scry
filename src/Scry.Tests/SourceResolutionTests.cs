using Microsoft.EntityFrameworkCore.Query;

/// <summary>
/// A source is resolved on every request that reads it — as the root, a join's inner side, a set
/// operand, a membership set, a policied traversal — through a delegate closed over its set once at
/// startup rather than a reflective invoke per resolution. What the delegate has to keep is what the
/// invoke had: the set is the request's own context's, never one held across requests.
/// </summary>
[TestFixture]
public class SourceResolutionTests
{
    static readonly Schema schema = Build();

    static Schema Build()
    {
        var options = new ScryOptions(typeof(TestContext));
        options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        return Schema.Build(options);
    }

    [Test]
    public void AnEntitySourceResolvesToItsSet()
    {
        using var context = TestContext.CreateSeeded();

        var query = Source("Employee").Resolve(context, EmptyServiceProvider.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(query.ElementType, Is.EqualTo(typeof(Employee)));
            Assert.That(query.Provider, Is.InstanceOf<IAsyncQueryProvider>());
        });
    }

    // The delegate binds the context it is given rather than capturing one: a context answers with its
    // one set however often it is asked, and another context answers with its own.
    [Test]
    public void TheSetIsTheContextsOwn()
    {
        using var first = TestContext.CreateSeeded();
        using var second = TestContext.CreateSeeded();
        var source = Source("Employee");

        Assert.Multiple(() =>
        {
            Assert.That(
                source.Resolve(first, EmptyServiceProvider.Instance),
                Is.SameAs(source.Resolve(first, EmptyServiceProvider.Instance)));
            Assert.That(
                source.Resolve(first, EmptyServiceProvider.Instance),
                Is.Not.SameAs(source.Resolve(second, EmptyServiceProvider.Instance)));
        });
    }

    // A derived POCO reads its base's registered rows narrowed by type, through a delegate of the same
    // kind over OfType.
    [Test]
    public void ADerivedPocoResolvesToTheBaseRowsNarrowed()
    {
        using var context = TestContext.CreateSeeded();

        var query = Source("PublicHoliday").Resolve(context, EmptyServiceProvider.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(query.ElementType, Is.EqualTo(typeof(PublicHoliday)));
            Assert.That(query.Cast<object>().Count(), Is.EqualTo(Holiday.Seed().OfType<PublicHoliday>().Count()));
        });
    }

    static ScrySource Source(string name)
    {
        Assert.That(schema.TryGetSource(name, out var source), Is.True);
        return source!;
    }
}
