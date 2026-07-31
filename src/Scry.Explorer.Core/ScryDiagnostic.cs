namespace Scry;

/// <summary>A Roslyn diagnostic within the user's code: message, span (in editor coordinates), severity.</summary>
public sealed record ScryDiagnostic(string Message, int Start, int End, bool IsError);