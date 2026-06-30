namespace Scry.Tests;

/// <summary>
/// Builds the shared LocalDB database once for the whole assembly (see <see cref="TestContext"/>),
/// and disposes it when the run completes.
/// </summary>
[SetUpFixture]
public class DatabaseSetup
{
    [OneTimeSetUp]
    public Task SetUp() => TestContext.InitializeAsync();

    [OneTimeTearDown]
    public Task TearDown() => TestContext.ShutdownAsync();
}
