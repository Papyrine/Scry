/// <summary>A built projection selector plus the JSON paths each array slot maps to.</summary>
sealed class ProjectionPlan(
    LambdaExpression selector,
    IReadOnlyList<IReadOnlyList<string>> shape,
    IReadOnlyList<bool>? binarySlots = null)
{
    public LambdaExpression Selector { get; } = selector;

    public IReadOnlyList<IReadOnlyList<string>> Shape { get; } = shape;

    /// <summary>
    /// Per-slot: whether the slot is a member path terminating at a <c>[BinaryTransfer]</c> member,
    /// whose values divert to raw multipart parts when a collector is in scope. Null when no slot is —
    /// the common case, and the check the writers branch on.
    /// </summary>
    public IReadOnlyList<bool>? BinarySlots { get; } = binarySlots;

    PlanShapeWriter? writer;

    /// <summary>
    /// The row writer for this shape. Built on first use and kept — a cached plan shares its
    /// projection across every request of the shape, so names are camel-cased and escaped once, not
    /// per row. A racing double build produces identical writers, so the bare assignment is benign.
    /// </summary>
    public PlanShapeWriter Writer =>
        writer ??= PlanShapeWriter.Create(Shape, BinarySlots);
}
