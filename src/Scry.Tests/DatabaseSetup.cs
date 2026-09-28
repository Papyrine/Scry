/// <summary>
/// Builds the shared LocalDB database once for the whole assembly (see <see cref="TestContext"/>),
/// and disposes it when the run completes.
/// </summary>
public static class DatabaseSetup
{
    [Before(TestSession)]
    public static Task SetUp() => TestContext.InitializeAsync();

    [After(TestSession)]
    public static Task TearDown() => TestContext.ShutdownAsync();
}
