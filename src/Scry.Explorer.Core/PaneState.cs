namespace Scry;

/// <summary>
/// One resizable pane pair's split: the first pane's share of the container, 0..1. Kept as a class so
/// the ratio can be persisted and rehydrated.
/// </summary>
public sealed class PaneState(double defaultRatio, double minimum = 0.1, double maximum = 0.9)
{
    public double DefaultRatio { get; } = defaultRatio;

    public double Ratio { get; set; } = defaultRatio;

    /// <summary>
    /// Takes a drag position as a share of the container and clamps it. A pane dragged past either end
    /// keeps a usable sliver rather than vanishing into an edge that cannot be grabbed again.
    /// </summary>
    public void Drag(double fraction) =>
        Ratio = Math.Clamp(fraction, minimum, maximum);

    /// <summary>Back to the default split — what double-clicking the drag bar does.</summary>
    public void Reset() =>
        Ratio = DefaultRatio;

    /// <summary>
    /// The ratio as an inline flex-grow style. The panes either side of a resizer are laid out as
    /// <c>flex: {ratio} 1 0%</c> and <c>flex: {1 - ratio} 1 0%</c>, so the split is one number.
    /// </summary>
    public string Grow() =>
        Grow(Ratio);

    /// <summary>The flex-grow style for an arbitrary share. See <see cref="Grow()"/>.</summary>
    public static string Grow(double ratio) =>
        FormattableString.Invariant($"flex: {ratio} 1 0%");

    /// <summary>Reads a persisted ratio, falling back to the default for anything unusable.</summary>
    public void Load(string? stored)
    {
        if (double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) &&
            double.IsFinite(ratio))
        {
            Drag(ratio);
            return;
        }

        Reset();
    }

    public string Serialize() =>
        Ratio.ToString("R", CultureInfo.InvariantCulture);
}
