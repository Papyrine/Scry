/// <summary>
/// The list of units an answer was made of, as something with an address of its own: every unit in
/// order, with the rows it was read from. Two answers of the same rows are the same list, so the
/// record keeps the list once and has each answer name it.
/// </summary>
static class DisclosureManifest
{
    /// <summary>
    /// Whether a batch is an answer from its beginning to its end, handed on whole: the one case in
    /// which the list it carries is the list the answer was made of.
    /// </summary>
    public static bool Whole(ScryDisclosureBatch batch) =>
        batch is {Begin: not null, Units.Count: > 0, Close.Outcome: ScryDisclosureOutcome.Released} &&
        batch.Close.Units == batch.Units.Count;

    /// <summary>
    /// The address of the list a batch carries. Over each unit's place and content and each of its
    /// rows' source, key and way of being reached — everything the record keeps of the list, so that
    /// two lists with one address are one list. The units' content alone would not do: two different
    /// rows can be sent as the same bytes.
    /// </summary>
    public static byte[] Address(ScryDisclosureBatch batch)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[sizeof(int)];
        var rows = batch.Entities
            .OrderBy(_ => _.Ordinal)
            .ThenBy(_ => _.Slot)
            .ToLookup(_ => _.Ordinal);
        foreach (var unit in batch.Units.OrderBy(_ => _.Ordinal))
        {
            Number(hash, number, unit.Ordinal);
            hash.AppendData(unit.Content.ToArray());
            var read = rows[unit.Ordinal].ToList();
            Number(hash, number, read.Count);
            foreach (var row in read)
            {
                Number(hash, number, row.Slot);
                Text(hash, number, row.Source);
                Text(hash, number, row.Key);
                Text(hash, number, row.Via);
            }
        }

        return hash.GetHashAndReset();
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
