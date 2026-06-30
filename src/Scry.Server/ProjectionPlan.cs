/// <summary>A built projection selector plus the JSON paths each array slot maps to.</summary>
sealed class ProjectionPlan(LambdaExpression selector, IReadOnlyList<IReadOnlyList<string>> shape)
{
    public LambdaExpression Selector { get; } = selector;
    public IReadOnlyList<IReadOnlyList<string>> Shape { get; } = shape;
}