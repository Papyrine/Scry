/// <summary>
/// A source the context does not map is a 500 on every query of it after a startup that passed:
/// introspection advertises it, and <c>Set&lt;T&gt;()</c> refuses it. The startup check names it
/// instead. The test model carries two such types on purpose — they pin classification — which is
/// what makes the shared processor the fixture here.
/// </summary>
[TestFixture]
public class SourceMappingTests
{
    [Test]
    public void AnOptedInTypeTheContextDoesNotMapIsRefusedAtStartup()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<Exception>(() => SharedProcessor.Instance.EnsureSourcesMapped(context))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain("does not map it"));
            Assert.That(exception.Message, Does.Contain("TestContext"));
            Assert.That(exception.Message, Does.Contain("AddPocoSource"));
        });
    }

    // One model assembly may serve several contexts, each opting in types the others map; the host
    // says so, and the check stands down.
    [Test]
    public void TheRefusalIsWaivedForAnAssemblyServingSeveralContexts()
    {
        using var context = TestContext.CreateSeeded();
        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AllowUnmappedSources = true;
        });

        Assert.DoesNotThrow(() => processor.EnsureSourcesMapped(context));
    }

    // Where the check is waived, a query naming an unmapped source is a rejection like any unknown
    // source — never the Set<T>() fault, which a client could otherwise produce on demand.
    [Test]
    public void AQueryOfAnUnmappedSourceIsRejectedNotFaulted()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Region", [new CountOp()]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context))!;

        Assert.That(exception.Message, Is.EqualTo("Unknown source 'Region'."));
    }

    // The refusal is per source, so the message names the one that is missing.
    [Test]
    public void TheRefusalNamesTheSource()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<Exception>(() => SharedProcessor.Instance.EnsureSourcesMapped(context))!;

        Assert.That(exception.Message, Does.Match("Source '(DepartmentHeadcount|Region)'"));
    }
}
