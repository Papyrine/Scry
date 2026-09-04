namespace Scry;

/// <summary>
/// A result payload arranged as a grid: the column names, the cells as rendered, and the server's own
/// rows kept alongside them for the exports that preserve nesting.
/// </summary>
/// <param name="Columns">Member names, taken from the first object in the payload.</param>
/// <param name="Rows">The cells as displayed, one list per row, in column order.</param>
/// <param name="PayloadRows">The rows exactly as the server sent them.</param>
/// <param name="IsFlat">
/// Whether every cell is a scalar. A cell that is itself an object or an array came from a projection
/// into a navigation, which makes the result a tree rather than a grid — and a grid cannot hold that
/// without flattening the shape away, which is what decides whether CSV is on offer.
/// </param>
public sealed record ResultTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<JsonElement> PayloadRows,
    bool IsFlat)
{
    /// <summary>
    /// The grid for a list payload, or null when the payload is not an array. Non-object entries are
    /// skipped rather than rendered as a row with no members.
    /// </summary>
    public static ResultTable? FromList(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var columns = new List<string>();
        var rows = new List<JsonElement>();
        foreach (var row in payload.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (columns.Count == 0)
            {
                columns.AddRange(row.EnumerateObject().Select(_ => _.Name));
            }

            rows.Add(row);
        }

        return Build(columns, rows);
    }

    /// <summary>
    /// One projected object as a single-row grid, so a <c>Single</c> result renders through the same
    /// markup a list does.
    /// </summary>
    public static ResultTable FromRow(JsonElement row) =>
        Build(row.EnumerateObject().Select(_ => _.Name).ToList(), [row]);

    static ResultTable Build(IReadOnlyList<string> columns, IReadOnlyList<JsonElement> rows) =>
        new(
            columns,
            rows
                .Select(IReadOnlyList<string> (_) => _.EnumerateObject()
                    .Select(_ => _.Value.ToString())
                    .ToList())
                .ToList(),
            rows,
            rows.All(_ => _.EnumerateObject()
                .All(_ => _.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))));
}
