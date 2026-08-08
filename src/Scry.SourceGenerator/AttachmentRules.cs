/// <summary>
/// The rules an attachment brings: it is not a value, it cannot be carried through an operator that
/// rewrites what a row is, and projecting one obliges the query to project its row's key beside it.
/// Reported as errors — see <c>LinqDiagnostics.Error</c> — because unlike the other rules these
/// describe a result that cannot be built at all rather than one the server would reject.
/// </summary>
/// <remarks>
/// Partial in the same way the rest of the analyzer is: the projection is read as written, so one
/// composed through a helper is left to <c>QueryTranslator</c>, which enforces every rule here again
/// when the query is captured.
/// </remarks>
static class AttachmentRules
{
    // Operators that rewrite what a row is. A key projected beside an attachment stops identifying one
    // row of one source once any of them has run.
    static readonly HashSet<string> refused = new(StringComparer.Ordinal)
    {
        "Distinct",
        "DistinctBy",
        "SelectMany",
        "Join",
        "GroupJoin",
        "GroupBy",
        "Union",
        "UnionBy",
        "Concat",
        "Intersect",
        "IntersectBy",
        "Except",
        "ExceptBy"
    };

    /// <summary>
    /// Checks one link's wired lambdas. A projection is held to the key rule; every other lambda —
    /// a predicate, an ordering key, a group key — may not name an attachment at all.
    /// </summary>
    public static void Check(
        OperationAnalysisContext context,
        IInvocationOperation link,
        string name,
        KnownTypes known,
        bool rewritten)
    {
        if (known.Attachment is null)
        {
            return;
        }

        if (name == "Select")
        {
            Projection(context, link, known, rewritten);
            return;
        }

        // Anywhere else an attachment is read, it is being used as a value.
        foreach (var lambda in ExpressionRules.Wired(link))
        {
            foreach (var reference in Attachments(lambda, known))
            {
                Report(context, LinqDiagnostics.AttachmentAsValue, reference, reference.Property.Name);
                return;
            }
        }
    }

    /// <summary>
    /// Reports a chain that would carry an attachment through an operator that rewrites its rows.
    /// Read off the chain rather than the projection, since a whole-model query carries its model's
    /// attachments without naming any of them.
    /// </summary>
    public static void Operator(OperationAnalysisContext context, IInvocationOperation link, string name) =>
        context.ReportDiagnostic(Diagnostic.Create(LinqDiagnostics.AttachmentOperator, QueryChain.Where(link), name));

    public static bool Rewrites(string name) =>
        refused.Contains(name);

    /// <summary>
    /// Whether a row type carries an attachment anywhere in its shape — what makes a whole-model query
    /// one that would carry handles.
    /// </summary>
    public static bool Carries(ITypeSymbol? type, KnownTypes known, int depth = 0)
    {
        if (type is null ||
            depth > 4 ||
            known.Attachment is null)
        {
            return false;
        }

        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol property)
            {
                continue;
            }

            if (known.IsAttachment(property.Type))
            {
                return true;
            }

            if (known.IsModel(property.Type) &&
                Carries(property.Type, known, depth + 1))
            {
                return true;
            }
        }

        return type.BaseType is { } baseType && Carries(baseType, known, depth);
    }

    static void Projection(OperationAnalysisContext context, IInvocationOperation link, KnownTypes known, bool rewritten)
    {
        foreach (var lambda in ExpressionRules.Wired(link))
        {
            foreach (var reference in Attachments(lambda, known))
            {
                if (rewritten)
                {
                    // The operator that rewrote the rows has already been reported; saying the key is
                    // missing too would only describe a query that cannot work either way.
                    return;
                }

                // The row the attachment hangs off: the query's own where it is read directly, or the
                // navigation traversed to reach it.
                var owner = Owner(reference);
                var keys = known.KeysOf(reference.Instance?.Type ?? reference.Property.ContainingType);
                foreach (var key in keys)
                {
                    if (!Projects(lambda, owner, key, known))
                    {
                        Report(
                            context,
                            LinqDiagnostics.AttachmentKeys,
                            reference,
                            reference.Property.Name,
                            owner.Length == 0 ? key : $"{owner}.{key}");
                        return;
                    }
                }
            }
        }
    }

    // The path to the row an attachment hangs off, written as the query wrote it: empty when read
    // straight off the lambda parameter, else the navigation chain (e.g. "Manager").
    static string Owner(IPropertyReferenceOperation reference)
    {
        var segments = new List<string>();
        for (var current = reference.Instance; current is IPropertyReferenceOperation owner; current = owner.Instance)
        {
            segments.Insert(0, owner.Property.Name);
        }

        return string.Join(".", segments);
    }

    // Whether the projection reads the named key off the same row. Only plain member reads count: a
    // computed leaf is not the key, whatever it was computed from.
    static bool Projects(IAnonymousFunctionOperation lambda, string owner, string key, KnownTypes known)
    {
        foreach (var operation in ExpressionRules.Descendants(lambda))
        {
            if (operation is IPropertyReferenceOperation property &&
                !known.IsAttachment(property.Type) &&
                property.Property.Name == key &&
                Owner(property) == owner)
            {
                return true;
            }
        }

        return false;
    }

    static IEnumerable<IPropertyReferenceOperation> Attachments(IAnonymousFunctionOperation lambda, KnownTypes known)
    {
        foreach (var operation in ExpressionRules.Descendants(lambda))
        {
            if (operation is IPropertyReferenceOperation property &&
                known.IsAttachment(property.Type))
            {
                yield return property;
            }
        }
    }

    static void Report(
        OperationAnalysisContext context,
        DiagnosticDescriptor descriptor,
        IOperation operation,
        params object[] arguments) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, operation.Syntax.GetLocation(), arguments));
}
