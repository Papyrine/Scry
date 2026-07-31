namespace Scry;

/// <summary>Hover (QuickInfo) text for the symbol at a position, plus the span it covers (editor coords).</summary>
public sealed record ScryHover(string Text, int Start, int End);