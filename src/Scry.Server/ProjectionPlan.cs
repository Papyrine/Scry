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
    /// The row writer for this shape, so names are camel-cased and escaped once rather than per row.
    /// Held by shape rather than by plan: a plan is built per request, and the writer outlives it.
    /// </summary>
    /// <remarks>
    /// Kept in this field as well, so a plan that writes more than once — a page reads its rows and
    /// writes them, a list writes as it reads — looks the shape up once. A racing double read returns
    /// the same writer, so the bare assignment is benign.
    /// </remarks>
    public PlanShapeWriter Writer =>
        writer ??= PlanShapeWriter.Get(Shape, BinarySlots);
}
