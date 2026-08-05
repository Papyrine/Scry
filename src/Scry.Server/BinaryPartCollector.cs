/// <summary>
/// Accumulates the raw binary values a response diverts to multipart parts, in emission order. One
/// per HTTP call — a batch threads one collector through every entry, which is what numbers its parts
/// globally. Null in scope means never divert: the non-HTTP surface and every path that predates
/// binary transfer stay bit-identical by construction.
/// </summary>
sealed class BinaryPartCollector
{
    readonly List<byte[]> parts = [];

    public IReadOnlyList<byte[]> Parts => parts;

    public int Count => parts.Count;

    /// <summary>Adds a diverted value, returning the index its placeholder references it by.</summary>
    public int Add(byte[] bytes)
    {
        parts.Add(bytes);
        return parts.Count - 1;
    }

    /// <summary>
    /// Removes and returns the accumulated parts — the streaming endpoint's per-row drain, which is
    /// what resets placeholder indices after every row line.
    /// </summary>
    public byte[][] Drain()
    {
        var drained = parts.ToArray();
        parts.Clear();
        return drained;
    }
}

/// <summary>
/// The JSON form a diverted binary value takes on the general (dictionary-shaping) path: serializes
/// to exactly <c>{"$bin":n}</c>, matching the fast writer's hand-written bytes.
/// </summary>
sealed record BinaryPlaceholder([property: JsonPropertyName(ScryBinary.PartProperty)] int Index);
