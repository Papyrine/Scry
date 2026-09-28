using Microsoft.EntityFrameworkCore.Query;

/// <summary>
/// A source is resolved on every request that reads it — as the root, a join's inner side, a set
/// operand, a membership set, a policied traversal — through a delegate closed over its set once at
/// startup rather than a reflective invoke per resolution. What the delegate has to keep is what the
/// invoke had: the set is the request's own context's, never one held across requests.
/// </summary>
public class SourceResolutionTests
{
    static Schema schema = Build();

    static Schema Build()
    {
        var options = new ScryOptions(typeof(TestContext));
        options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        return Schema.Build(options);
    }

    [Test]
    public async Task AnEntitySourceResolvesToItsSet()
    {
        using var context = TestContext.CreateSeeded();

        var query = (await Source("Employee")).Resolve(context, EmptyServiceProvider.Instance);

        using (Assert.Multiple())
        {
            await Assert.That(query.ElementType).IsEqualTo(typeof(Employee));
            await Assert.That(query.Provider).IsAssignableTo<IAsyncQueryProvider>();
        }
    }

    // The delegate binds the context it is given rather than capturing one: a context answers with its
    // one set however often it is asked, and another context answers with its own.
    [Test]
    public async Task TheSetIsTheContextsOwn()
    {
        using var first = TestContext.CreateSeeded();
        using var second = TestContext.CreateSeeded();
        var source = await Source("Employee");

        using (Assert.Multiple())
        {
            await Assert.That(source.Resolve(first, EmptyServiceProvider.Instance)).IsSameReferenceAs(source.Resolve(first, EmptyServiceProvider.Instance));
            await Assert.That(source.Resolve(first, EmptyServiceProvider.Instance)).IsNotSameReferenceAs(source.Resolve(second, EmptyServiceProvider.Instance));
        }
    }

    // A derived POCO reads its base's registered rows narrowed by type, through a delegate of the same
    // kind over OfType.
    [Test]
    public async Task ADerivedPocoResolvesToTheBaseRowsNarrowed()
    {
        using var context = TestContext.CreateSeeded();

        var query = (await Source("PublicHoliday")).Resolve(context, EmptyServiceProvider.Instance);

        using (Assert.Multiple())
        {
            await Assert.That(query.ElementType).IsEqualTo(typeof(PublicHoliday));
            await Assert.That(query.Cast<object>().Count()).IsEqualTo(Holiday.Seed().OfType<PublicHoliday>().Count());
        }
    }

    static async Task<ScrySource> Source(string name)
    {
        await Assert.That(schema.TryGetSource(name, out var source)).IsTrue();
        return source!;
    }
}
