/// <summary>A built projection selector plus the JSON paths each array slot maps to.</summary>
sealed class ProjectionPlan(LambdaExpression selector, IReadOnlyList<IReadOnlyList<string>> shape)
{
    public LambdaExpression Selector { get; } = selector;

    public IReadOnlyList<IReadOnlyList<string>> Shape { get; } = shape;

    PlanShapeWriter? writer;

    /// <summary>
    /// The row writer for this shape. Built on first use and kept — a cached plan shares its
    /// projection across every request of the shape, so names are camel-cased and escaped once, not
    /// per row. A racing double build produces identical writers, so the bare assignment is benign.
    /// </summary>
    public PlanShapeWriter Writer =>
        writer ??= PlanShapeWriter.Create(Shape);
}
