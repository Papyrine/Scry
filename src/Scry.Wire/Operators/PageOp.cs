namespace Scry.Wire;

/// <summary>
/// Terminal: returns a bounded page of rows plus whether more exist. <see cref="Size"/> is the
/// requested page size; when null the server applies its <c>DefaultPageSize</c>. Either way the
/// effective size is capped by <c>MaxPageSize</c>.
/// </summary>
public sealed record PageOp(int? Size) :
    QueryOp;
