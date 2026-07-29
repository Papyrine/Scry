/// <summary>
/// The processor shared by every test that runs the default configuration. A ScryProcessor is
/// stateless and thread-safe — production registers it as a singleton — and building one reflects
/// over the whole model assembly, so per-fixture instances only re-paid that cost. Tests that need
/// custom options (policies, limits) still build their own.
/// </summary>
public static class SharedProcessor
{
    // begin-snippet: processorCreate
    public static ScryProcessor Instance { get; } = ScryProcessor.Create<TestContext>(
        options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
    // end-snippet
}
