namespace Scry;

/// <summary>
/// One renamed enum value: the name the server currently serializes it under, and the previous names
/// it was exposed as. Attached to a <see cref="QueryResponse"/> for a drifted client, whose reader
/// resolves an unknown value name to a previous name it does know — the response-side counterpart of
/// the server accepting previous names on the request.
/// </summary>
public sealed record EnumAlias(string EnumName, string ValueName, IReadOnlyList<string> PreviousNames);
