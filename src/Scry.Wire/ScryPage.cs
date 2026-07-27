namespace Scry.Wire;

/// <summary>
/// A bounded page of query results: the rows for this page, whether a further page exists, and an
/// opaque cursor to resume from. The cursor is null until keyset paging lands; offset paging advances
/// with <c>Skip</c> and relies on <see cref="HasMore"/>.
/// </summary>
public sealed record ScryPage<T>(IReadOnlyList<T> Items, bool HasMore, string? Cursor);
