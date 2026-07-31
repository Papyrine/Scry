namespace Scry;

/// <summary>
/// Terminal: returns a bounded page of rows plus whether more exist. <see cref="Size"/> is the
/// requested page size; when null the server applies its <c>DefaultPageSize</c>. Either way the
/// effective size is capped by <c>MaxPageSize</c>. <see cref="Cursor"/> is an opaque resume token
/// from a previous page's response; when set the server seeks past it (keyset paging) instead of
/// starting from the beginning. Clients must not parse or synthesize a cursor.
/// </summary>
public sealed record PageOp(int? Size, string? Cursor = null) :
    QueryOp;
