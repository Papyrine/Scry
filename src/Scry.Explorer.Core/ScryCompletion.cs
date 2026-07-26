namespace Scry.Explorer.Core;

/// <summary>A completion offered by Roslyn: its label, Roslyn tag (kind), and the span it replaces.</summary>
public sealed record ScryCompletion(string Label, string Kind, int ReplaceStart, int ReplaceEnd);