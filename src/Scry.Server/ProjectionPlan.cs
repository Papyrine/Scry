/// <summary>A built projection selector plus the JSON paths each array slot maps to.</summary>
sealed class ProjectionPlan(
    LambdaExpression selector,
    IReadOnlyList<IReadOnlyList<string>> shape,
    IReadOnlyList<bool>? binarySlots = null,
    IReadOnlyList<DisclosureSlot>? entities = null,
    IReadOnlyList<bool>? builtLeftOut = null)
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

    /// <summary>
    /// Per written slot: whether its value is read from a member the disclosure audit leaves out, or
    /// from a row of a source it leaves out, so that the record holds a marker in its place. Null
    /// where no slot is, which is every plan of a host that leaves nothing out. Read only where the
    /// audit is on.
    /// </summary>
    /// <remarks>
    /// Decided from what each leaf's expression reads, so a value computed from an excluded member —
    /// its length, a string it was joined into — is left out as the member is. A leaf that reads one
    /// alongside other members is left out whole: what it holds cannot be told apart from the part
    /// that came from the excluded member. And from what building each leaf was found to read, for
    /// what its expression does not say: a group key made from an excluded member, a row that is an
    /// element of one, the other side of a set operation.
    /// </remarks>
    public IReadOnlyList<bool>? LeftOut(ScryDisclosureOptions settings)
    {
        if (settings is {ExcludedMembers.Count: 0, Excluded.Count: 0})
        {
            return null;
        }

        if (leftOutKnown)
        {
            return leftOut;
        }

        leftOut = Reads(settings);
        leftOutKnown = true;
        return leftOut;
    }

    bool leftOutKnown;
    IReadOnlyList<bool>? leftOut;
    IReadOnlyList<bool>? built = builtLeftOut;

    /// <summary>A slot is left out where either list says so.</summary>
    public static IReadOnlyList<bool>? Either(IReadOnlyList<bool>? left, IReadOnlyList<bool>? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return [.. left.Select((_, slot) => _ || (slot < right.Count && right[slot]))];
    }

    IReadOnlyList<bool>? Reads(ScryDisclosureOptions settings)
    {
        var written = Shape.Count;
        var slots = new bool[written];
        var any = false;
        for (var slot = 0; slot < written && built is not null && slot < built.Count; slot++)
        {
            any |= slots[slot] = built[slot];
        }

        var body = Selector.Body;
        if (body is NewArrayExpression array)
        {
            for (var slot = 0; slot < written && slot < array.Expressions.Count; slot++)
            {
                if (!slots[slot] &&
                    LeftOutReads.Find(array.Expressions[slot], settings))
                {
                    slots[slot] = any = true;
                }
            }
        }
        else if (body is NewExpression row)
        {
            for (var slot = 0; slot < written && slot < row.Arguments.Count; slot++)
            {
                if (!slots[slot] &&
                    LeftOutReads.Find(row.Arguments[slot], settings))
                {
                    slots[slot] = any = true;
                }
            }
        }

        // A selector of a shape this does not know is left out whole where it reads one at all,
        // which keeps out more than it need and never less.
        else if (any ||
                 LeftOutReads.Find(body, settings))
        {
            Array.Fill(slots, true);
            any = true;
        }

        if (any)
        {
            return slots;
        }

        return null;
    }

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
