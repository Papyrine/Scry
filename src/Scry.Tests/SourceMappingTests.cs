/// <summary>
/// A source the context does not map is a 500 on every query of it after a startup that passed:
/// introspection advertises it, and <c>Set&lt;T&gt;()</c> refuses it. The startup check names it
/// instead. The test model carries two such types on purpose — they pin classification — which is
/// what makes the shared processor the fixture here.
/// </summary>
public class SourceMappingTests
{
    [Test]
    public async Task AnOptedInTypeTheContextDoesNotMapIsRefusedAtStartup()
    {
        await using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<Exception>(() => SharedProcessor.Instance.EnsureSourcesMapped(context));

        using (Assert.Multiple())
        {
            await Assert.That(exception.Message).Contains("does not map it");
            await Assert.That(exception.Message).Contains("TestContext");
            await Assert.That(exception.Message).Contains("AddPocoSource");
        }
    }

    // One model assembly may serve several contexts, each opting in types the others map; the host
    // says so, and the check stands down.
    [Test]
    public async Task TheRefusalIsWaivedForAnAssemblyServingSeveralContexts()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AllowUnmappedSources = true;
        });

        await Assert.That(() => processor.EnsureSourcesMapped(context)).ThrowsNothing();
    }

    // Where the check is waived, a query naming an unmapped source is a rejection like any unknown
    // source — never the Set<T>() fault, which a client could otherwise produce on demand.
    [Test]
    public async Task AQueryOfAnUnmappedSourceIsRejectedNotFaulted()
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Region", [new CountOp()]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        await Assert.That(exception.Message).IsEqualTo("Unknown source 'Region'.");
    }

    // The refusal is per source, so the message names the one that is missing.
    [Test]
    public async Task TheRefusalNamesTheSource()
    {
        await using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<Exception>(() => SharedProcessor.Instance.EnsureSourcesMapped(context));

        await Assert.That(exception.Message).Matches("Source '(DepartmentHeadcount|Region)'");
    }
}
