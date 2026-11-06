/// <summary>What a handler answered with, set through <c>SetScryResult</c>.</summary>
sealed class CommandResult
{
    public string? Json { get; set; }

    /// <summary>The rows the result was made from, as <see cref="ScryDisclosureRow.Serialize"/> wrote them.</summary>
    public string? Rows { get; set; }
}
