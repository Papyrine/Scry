namespace Scry.Tests;

[TestFixture]
public class IntrospectionTests
{
    [Test]
    public Task Describe()
    {
        var processor = ScryProcessor.Create(options =>
        {
            options.UseModel<TestContext>();
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        });

        return Verify(processor.Describe());
    }
}
