/// <summary>
/// The units a batch carries, as a list with an address of its own, made of runs that each have one
/// too. Two batches of the same units are the same list, and two that differ by a row are mostly the
/// same runs, so the record keeps each run once and each list as the runs it is made of.
/// </summary>
/// <remarks>
/// Where one run ends and the next begins is decided by the units themselves and not by how far into
/// the list they are: a run ends on a unit whose digest says so. A row added, changed or taken away
/// then moves the ends of the run it is in and of no other, and every run after it is the run it was.
/// Cut at every so many units, one row more near the start would make every later run a new one.
/// </remarks>
sealed class DisclosureManifest
{
    // A run ends on one unit in this many, by its digest, and so is this long on average.
    const uint Every = 32;

    // And is never longer than this, whatever the digests say: units that are all the same have one
    // digest between them, which either ends every run or ends none.
    const int Longest = 256;

    DisclosureManifest(int start, byte[] address, List<ScryDisclosureUnit> units, ILookup<int, ScryDisclosureEntity> rows, List<DisclosureRun> runs)
    {
        Start = start;
        Address = address;
        Units = units;
        Rows = rows;
        Runs = runs;
    }

    /// <summary>The place of the list's first unit in the answer it is part of.</summary>
    public int Start { get; }

    /// <summary>
    /// The list's address: over its runs' addresses in order. Not over where in an answer it was, so
    /// the same units sent as a later part of a longer answer are the same list.
    /// </summary>
    public byte[] Address { get; }

    /// <summary>The units, in order.</summary>
    public List<ScryDisclosureUnit> Units { get; }

    /// <summary>The rows each unit was read from, by the unit's place in the answer.</summary>
    public ILookup<int, ScryDisclosureEntity> Rows { get; }

    public List<DisclosureRun> Runs { get; }

    /// <summary>
    /// The list a batch carries, or null where it carries no units or carries units that do not
    /// follow one another — which no answer is recorded as, and which is kept as it arrived.
    /// </summary>
    public static DisclosureManifest? Of(ScryDisclosureBatch batch)
    {
        if (batch.Units.Count == 0)
        {
            return null;
        }

        var units = batch.Units.OrderBy(_ => _.Ordinal).ToList();
        var start = units[0].Ordinal;
        for (var index = 0; index < units.Count; index++)
        {
            if (units[index].Ordinal != start + index)
            {
                return null;
            }
        }

        var rows = batch.Entities
            .OrderBy(_ => _.Slot)
            .ToLookup(_ => _.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[sizeof(int)];
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        var runs = new List<DisclosureRun>();
        var from = 0;
        using var run = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < units.Count; index++)
        {
            Digest(hash, number, units[index], rows[units[index].Ordinal], digest);
            run.AppendData(digest);
            var length = index - from + 1;
            if (index < units.Count - 1 &&
                length < Longest &&
                BinaryPrimitives.ReadUInt32LittleEndian(digest) % Every != 0)
            {
                continue;
            }

            var address = run.GetHashAndReset();
            runs.Add(new(address, from, length));
            Number(whole, number, length);
            whole.AppendData(address);
            from = index + 1;
        }

        return new(start, whole.GetHashAndReset(), units, rows, runs);
    }

    // Everything the record keeps of one unit: its content, and each of its rows' source, key and
    // way of being reached. The content alone would not do: two different rows can be sent as the
    // same bytes. Not its place, which is what lets a run be the same run wherever it falls.
    static void Digest(IncrementalHash hash, Span<byte> number, ScryDisclosureUnit unit, IEnumerable<ScryDisclosureEntity> rows, Span<byte> digest)
    {
        hash.AppendData(unit.Content.ToArray());
        foreach (var row in rows)
        {
            Number(hash, number, row.Slot);
            Text(hash, number, row.Source);
            Text(hash, number, row.Key);
            Text(hash, number, row.Via);
        }

        hash.GetHashAndReset(digest);
    }

    static void Number(IncrementalHash hash, Span<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    // Each piece of text behind its length, so that where one ends and the next begins is part of
    // what is hashed.
    static void Text(IncrementalHash hash, Span<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Number(hash, buffer, bytes.Length);
        hash.AppendData(bytes);
    }
}

/// <summary>
/// One run of a list: its address, over the digests of its units in order, where in the list it
/// starts, and how many units it is.
/// </summary>
sealed record DisclosureRun(byte[] Address, int Start, int Units);
