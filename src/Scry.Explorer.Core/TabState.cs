namespace Scry;

/// <summary>One query tab: the text being edited, and a title if the user renamed it.</summary>
/// <remarks>
/// What a run produced is deliberately absent. A response is a fact about a moment — the rows a
/// server held then, under the policies that applied then — so restoring one on a later visit would
/// be showing something that may no longer be true, and quietly.
/// </remarks>
public sealed class TabState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Query { get; set; } = "";

    /// <summary>A title the user typed. Absent means the title is derived from the query.</summary>
    public string? Title { get; set; }
}