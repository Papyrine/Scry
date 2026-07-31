namespace Scry;

/// <summary>Takes at most a number of elements (capped by the server page-size limit).</summary>
public sealed record TakeOp(int Count) :
    QueryOp;