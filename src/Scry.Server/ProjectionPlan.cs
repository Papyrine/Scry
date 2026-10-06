/// <summary>A built projection selector plus the JSON paths each array slot maps to.</summary>
sealed class ProjectionPlan(
    LambdaExpression selector,
    IReadOnlyList<IReadOnlyList<string>> shape,
    IReadOnlyList<bool>? binarySlots = null,
    IReadOnlyList<DisclosureSlot>? entities = null)
{
    public LambdaExpression Selector { get; } = selector;

    /// <summary>
    /// Where the keys of the rows each projected row was read from lie, for the disclosure audit: in
    /// slots after every one <see cref="Shape"/> describes, so neither writer ever reaches them. Null
    /// where the audit is off, and where the rows are of no source that has a key — a grouping, a
    /// deduplicated projection.
    /// </summary>
    public IReadOnlyList<DisclosureSlot>? Entities { get; } = entities;

    public IReadOnlyList<IReadOnlyList<string>> Shape { get; } = shape;

    /// <summary>
    /// Per-slot: whether the slot is a member path terminating at a <c>[BinaryTransfer]</c> member,
    /// whose values divert to raw multipart parts when a collector is in scope. Null when no slot is —
    /// the common case, and the check the writers branch on.
    /// </summary>
    public IReadOnlyList<bool>? BinarySlots { get; } = binarySlots;

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
        field ??= PlanShapeWriter.Get(Shape, BinarySlots);

    /// <summary>
    /// Whether any written slot could hold a <c>byte[]</c>, which the disclosure audit records by
    /// digest where the wire carries the value. Read only where that audit is on.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="BinarySlots"/>, which marks only the paths ending at a
    /// <c>[BinaryTransfer]</c> member: a plain binary member travels inline as base64 and a computed
    /// leaf has no member at all, and both are bytes all the same. Decided from each leaf's static
    /// type, so a plan that says no has no row that says otherwise — which is what lets such a row be
    /// written once and its bytes serve as both the record and the response.
    /// </remarks>
    public bool HasBytes =>
        hasBytes ??= ReadsBytes(Selector.Body, Shape.Count);

    bool? hasBytes;

    static bool ReadsBytes(Expression body, int written)
    {
        // A shaped row: one boxed leaf per slot, the written ones first. The slots after them — a
        // page's cursor keys — are read and never written.
        if (body is NewArrayExpression array)
        {
            for (var slot = 0; slot < written && slot < array.Expressions.Count; slot++)
            {
                if (MayBeBytes(Unboxed(array.Expressions[slot])))
                {
                    return true;
                }
            }

            return false;
        }

        // A deduplicated row: one typed constructor argument per slot.
        if (body is NewExpression row)
        {
            return row.Arguments.Any(_ => MayBeBytes(_.Type));
        }

        // A selector of a shape this does not know is assumed to, which costs a second write and is
        // never wrong.
        return true;
    }

    static Type Unboxed(Expression leaf)
    {
        if (leaf is UnaryExpression {NodeType: ExpressionType.Convert} boxed &&
            boxed.Type == typeof(object))
        {
            return boxed.Operand.Type;
        }

        return leaf.Type;
    }

    // Anything a byte[] could be standing behind: its own type, and object or an interface it has.
    static bool MayBeBytes(Type type) =>
        type.IsAssignableFrom(typeof(byte[]));
}
