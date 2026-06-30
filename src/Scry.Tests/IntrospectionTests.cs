namespace Scry.Tests;

[TestFixture]
public class IntrospectionTests
{
    [Test]
    public Task Describe()
    {
        var processor = ScryProcessor.Create<TestContext>(
            _ => _.AddPocoSource<Holiday>(_ => Holiday.Seed()));

        return Verify(processor.Describe());
    }
}
