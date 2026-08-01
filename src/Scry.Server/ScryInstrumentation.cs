namespace Scry;

/// <summary>
/// The names Scry's server telemetry is published under. Nothing is emitted until something
/// subscribes — e.g. OpenTelemetry's <c>AddSource(ScryInstrumentation.ActivitySourceName)</c> for
/// traces and <c>AddMeter(ScryInstrumentation.MeterName)</c> for metrics. Registering an
/// <see cref="IScryAuditor"/> is the third, independent channel. See <c>docs/observability.md</c>
/// for every span, instrument, and tag.
/// </summary>
public static class ScryInstrumentation
{
    /// <summary>
    /// The <see cref="ActivitySource"/> name. One activity per query, spanning validation through
    /// shaping — and, for a streamed query, the whole read.
    /// </summary>
    public const string ActivitySourceName = "Scry.Server";

    /// <summary>The <see cref="Meter"/> name: the query duration and row-count histograms.</summary>
    public const string MeterName = "Scry.Server";
}
