sealed class TestOptions(Dictionary<string, string> values) :
    AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value) =>
        values.TryGetValue(key, out value!);
}