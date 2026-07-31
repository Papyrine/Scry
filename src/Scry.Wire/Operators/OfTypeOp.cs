namespace Scry;

/// <summary>
/// Narrows the sequence to the rows of a derived type, which every later operator then reads.
/// <see cref="Type"/> names that type exactly as a request's own root does, so it is resolved —
/// and <b>policy-filtered</b> — through the same allow-list.
/// </summary>
/// <remarks>
/// The name is resolved against the server's schema and checked to derive from the type currently
/// being queried; no CLR type ever comes off the wire. Narrowing composes with the row policies
/// already applied, because a derived type's rows are a subset of the base's.
/// </remarks>
public sealed record OfTypeOp(string Type) :
    QueryOp;
