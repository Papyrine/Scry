/// <summary>
/// The rules that apply inside a query lambda — what may be called on a value the row supplies.
///
/// Only lambdas typed as <c>Expression&lt;…&gt;</c> are read: those are the ones that reach the wire.
/// A lambda typed as a plain <c>Func&lt;…&gt;</c> on a Scry terminal — the key selector of
/// <c>ToDictionaryAsync</c>, say — runs on the client against rows already returned, so nothing it
/// calls has to be translatable.
///
/// Only calls that read the row are checked. A member of a scalar is held against the callable set;
/// a call outside any scalar — a helper, a <c>Parse</c>, an extension method, a delegate — has no
/// wire representation at all and is reported as client-side code. Sequence-shaped owners are left to
/// the translator: the same name over a collection, a group, or another source means something else
/// entirely — <c>Count</c> is a correlated subquery on a navigation, an aggregate over a group, and a
/// client-side terminal elsewhere — and any name over closure state is evaluated into a constant
/// before it reaches the wire. Guessing at those would cost the precision an analyzer has to keep.
/// </summary>
static class ExpressionRules
{
    // What a group can be folded to. Only used to tell a fold from a projection of the group itself.
    static readonly HashSet<string> folds = new(StringComparer.Ordinal)
    {
        "Count",
        "LongCount",
        "Sum",
        "Average",
        "Min",
        "Max",
        "Any",
        "All"
    };

    public static void Check(OperationAnalysisContext context, IInvocationOperation link)
    {
        foreach (var lambda in Wired(link))
        {
            foreach (var operation in Descendants(lambda))
            {
                switch (operation)
                {
                    case IInvocationOperation call:
                        Call(context, call, lambda);
                        break;
                    case IPropertyReferenceOperation property:
                        Property(context, property, lambda);
                        break;
                    case IInterpolatedStringOperation interpolated:
                        Interpolation(context, interpolated, lambda);
                        break;
                }
            }
        }
    }

    // The lambdas a call hands to the wire, told from the client-side ones by the parameter that
    // takes them: an Expression is carried, a Func is run here.
    public static IEnumerable<IAnonymousFunctionOperation> Wired(IInvocationOperation link)
    {
        var method = QueryChain.Unreduced(link);
        for (var index = 0; index < link.Arguments.Length; index++)
        {
            if (index >= method.Parameters.Length ||
                !IsExpression(method.Parameters[index].Type))
            {
                continue;
            }

            if (QueryChain.Lambda(link, index) is { } lambda)
            {
                yield return lambda;
            }
        }
    }

    static bool IsExpression(ITypeSymbol type) =>
        type is INamedTypeSymbol {IsGenericType: true, Name: "Expression"} named &&
        named.ContainingNamespace.ToDisplayString() == "System.Linq.Expressions";

    static void Call(OperationAnalysisContext context, IInvocationOperation call, IAnonymousFunctionOperation lambda)
    {
        var method = call.TargetMethod;

        // Reading a value as text is available on any scalar, so it is matched by name rather than by
        // owner. Only the argument-less form: a format has no translation that reads the same on every
        // connection.
        if (method.Name == "ToString")
        {
            if (method.Parameters.Length > 0 &&
                ReadsRow(call.Instance, lambda))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(LinqDiagnostics.FormattedToString, call.Syntax.GetLocation()));
            }

            return;
        }

        if (!Reads(call, lambda))
        {
            return;
        }

        // Equals is == spelled as a method, and the translator carries it as that comparison over any
        // operands — one on an instance, two on a static — refused by the comparison's own rules when
        // either is not a value. So it is clean on every owner, a Guid or an enum or a TimeSpan
        // included, not only the ones with functions of their own. The overloads taking a
        // StringComparison are not this: they ask for a case sensitivity, which is the string
        // surface's to answer below.
        if (method.Name == "Equals" &&
            !TakesComparison(method) &&
            method.Parameters.Length == (method.IsStatic ? 2 : 1))
        {
            return;
        }

        if (Owner(method.ContainingType) is { } owner)
        {
            if (SupportedLinq.IsFunction(owner, method.Name, method.Parameters.Length))
            {
                return;
            }

            // The function is carried, but not in this shape — Trim() translates and Trim(params char[])
            // has no SQL equivalent. Saying so beats naming the function as though none of it were
            // available.
            var name = SupportedLinq.IsFunctionName(owner, method.Name)
                ? $"this overload of {method.ContainingType.Name}.{method.Name}"
                : $"{method.ContainingType.Name}.{method.Name}";

            context.ReportDiagnostic(
                Diagnostic.Create(LinqDiagnostics.UnsupportedFunction, QueryChain.Where(call), name));
            return;
        }

        // A sequence speaks the translator's rule set — a subquery over a navigation, an aggregate
        // over a group, a membership test against a closure set — and is left to it.
        if (IsSequence(method.ContainingType))
        {
            return;
        }

        // The call reads the row, so it cannot be evaluated into a constant here — and it is not on
        // the callable surface, so it has no wire representation either. That leaves it nowhere to
        // run: the translator refuses exactly this shape.
        context.ReportDiagnostic(
            Diagnostic.Create(
                LinqDiagnostics.ClientSideCode,
                QueryChain.Where(call),
                $"{method.ContainingType.Name}.{method.Name}"));
    }

    // The owners whose members mean sequences rather than scalars: the LINQ operator surface, the
    // span extensions an array's Contains binds to, and anything that is itself a sequence — a
    // collection navigation, a group, a closure set. What may be called on those is the translator's
    // business, per the rules above.
    static bool IsSequence(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return true;
        }

        var name = type.ToDisplayString();
        if (name is SupportedLinq.Enumerable or SupportedLinq.Queryable or "System.MemoryExtensions")
        {
            return true;
        }

        if (IsEnumerable(type))
        {
            return true;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (IsEnumerable(implemented))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsEnumerable(INamedTypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;

    static void Property(OperationAnalysisContext context, IPropertyReferenceOperation property, IAnonymousFunctionOperation lambda)
    {
        var containing = property.Property.ContainingType;

        // Math contributes statics only, so a property on it is not a value read off the row.
        if (Owner(containing) is not { } owner ||
            owner == "System.Math" ||
            !ReadsRow(property.Instance, lambda) ||
            SupportedLinq.IsFunction(owner, property.Property.Name, 0))
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                LinqDiagnostics.UnsupportedFunction,
                property.Syntax.GetLocation(),
                $"{containing.Name}.{property.Property.Name}"));
    }

    // A plain hole means the same as a concatenation and is rewritten into one. A hole carrying a
    // format or an alignment is the ToString(format) case wearing different syntax.
    static void Interpolation(OperationAnalysisContext context, IInterpolatedStringOperation interpolated, IAnonymousFunctionOperation lambda)
    {
        foreach (var part in interpolated.Parts)
        {
            if (part is not IInterpolationOperation interpolation ||
                (interpolation.FormatString is null && interpolation.Alignment is null) ||
                !ReadsRow(interpolation.Expression, lambda))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(LinqDiagnostics.FormattedToString, part.Syntax.GetLocation()));
        }
    }

    static bool TakesComparison(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.ToDisplayString() == "System.StringComparison")
            {
                return true;
            }
        }

        return false;
    }

    // The scalar owners whose members are functions rather than member paths. Everything else — a
    // query model, a collection, a grouping — is left alone.
    static string? Owner(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return "System.String";
        }

        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return SupportedLinq.NullableOwner;
        }

        // The numeric owners exist for their Parse statics and their CompareTo — bool for its Parse
        // alone; any other member of theirs is then reported against the callable set rather than as
        // opaque client-side code.
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return "System." + type.MetadataName;
        }

        var name = type.ToDisplayString();
        if (name is "System.Math" or "System.Enum" or "System.Convert" or "System.TimeSpan")
        {
            return name;
        }

        if (SupportedLinq.Temporal.Contains(name))
        {
            return SupportedLinq.TemporalOwner;
        }

        return null;
    }

    // Whether a call touches the row at all, on either side: a string method reads it as its target,
    // one of Math's statics as its argument. A call that touches nothing is closure state, which is
    // evaluated into a constant before it ever reaches the wire.
    static bool Reads(IInvocationOperation call, IAnonymousFunctionOperation lambda)
    {
        if (ReadsRow(call.Instance, lambda))
        {
            return true;
        }

        foreach (var argument in call.Arguments)
        {
            if (ReadsRow(argument.Value, lambda))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an expression bottoms out at one of the lambda's own parameters — the row. A path
    /// rooted anywhere else is closure state.
    /// </summary>
    public static bool ReadsRow(IOperation? operation, IAnonymousFunctionOperation lambda)
    {
        while (operation is not null)
        {
            switch (operation)
            {
                case IParameterReferenceOperation reference:
                    return Owns(lambda, reference.Parameter);
                case IPropertyReferenceOperation property:
                    operation = property.Instance;
                    break;
                case IFieldReferenceOperation field:
                    operation = field.Instance;
                    break;
                case IInvocationOperation call:
                    operation = QueryChain.Source(call);
                    break;
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    static bool Owns(IAnonymousFunctionOperation lambda, IParameterSymbol parameter)
    {
        foreach (var candidate in lambda.Symbol.Parameters)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, parameter))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The single expression an expression-bodied lambda returns.</summary>
    public static IOperation? Body(IAnonymousFunctionOperation? lambda)
    {
        if (lambda is null)
        {
            return null;
        }

        foreach (var operation in lambda.Body.Operations)
        {
            var value = operation switch
            {
                IReturnOperation {ReturnedValue: { } returned} => returned,
                IExpressionStatementOperation statement => statement.Operation,
                _ => null
            };

            if (value is null)
            {
                continue;
            }

            while (value is IConversionOperation or IParenthesizedOperation)
            {
                value = value switch
                {
                    IConversionOperation conversion => conversion.Operand,
                    IParenthesizedOperation parenthesized => parenthesized.Operand,
                    _ => value
                };
            }

            return value;
        }

        return null;
    }

    /// <summary>Whether an expression constructs an object — the three ways C# spells a projection.</summary>
    public static bool Constructs(IOperation operation) =>
        operation is IAnonymousObjectCreationOperation or IObjectCreationOperation or ITupleOperation;

    /// <summary>Whether a reference to a group is the target of an aggregate folding it.</summary>
    public static bool IsFolded(IOperation reference)
    {
        var parent = reference.Parent;
        while (parent is IConversionOperation or IArgumentOperation or IParenthesizedOperation)
        {
            parent = parent.Parent;
        }

        return parent is IInvocationOperation call &&
               folds.Contains(call.TargetMethod.Name);
    }

    public static IEnumerable<IOperation> Descendants(IOperation operation)
    {
        foreach (var child in operation.ChildOperations)
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
