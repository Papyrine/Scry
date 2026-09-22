/// <summary>
/// Decides a query's capability members for one call: whether the caller may send each command at all,
/// and the row expression a targeted command's policy answers with. Each command's policy is asked once
/// per call, however many times — or rows — the query reads its capability.
/// </summary>
/// <remarks>
/// A live query is one call per run, so what a capability reads is decided again on every run: a row
/// policy's condition is re-evaluated in the database, and a command-wide decision that reads a claim
/// reaches the screen as soon as the next run asks.
/// </remarks>
sealed class CapabilityContext(Schema schema, ScryPolicyContext context)
{
    Dictionary<string, Decision> decided = new(StringComparer.Ordinal);

    /// <summary>
    /// The value of <paramref name="member"/> for the row <paramref name="owner"/>: false where the caller
    /// may not send the command at all, the policy's row condition read against the row where it has
    /// one, and true otherwise.
    /// </summary>
    public Expression Evaluate(Member member, Expression owner)
    {
        var decision = Decide(member.Command!);
        if (decision.Rows is not { } rows)
        {
            return Fixed(decision.Allowed);
        }

        return Inline(rows, owner);
    }

    /// <summary>
    /// A command policy's row condition read against <paramref name="owner"/>, with every value it wrote
    /// in as a literal bound as a parameter instead: a policy spelling a caller's id into its expression
    /// would otherwise be a statement — and a cached plan — per caller.
    /// </summary>
    public static Expression Inline(LambdaExpression rows, Expression owner)
    {
        var body = new ParameterReplacer(rows.Parameters[0], owner).Visit(rows.Body);
        return ConstantParameterizer.Instance.Visit(body);
    }

    /// <summary>
    /// A capability that is the same for every row, bound as a parameter the way a client's constant is.
    /// A bare constant would do in a filter, but a projection whose leaves are all constants is one the
    /// provider evaluates on the client whole — and refuses, as a constant it would have to hold.
    /// </summary>
    public static Expression Fixed(bool value) =>
        Parameterization.Parameterize(value, typeof(bool));

    Decision Decide(string name)
    {
        if (decided.TryGetValue(name, out var decision))
        {
            return decision;
        }

        schema.TryGetCommand(name, out var command);
        decision = Decide(command);
        decided[name] = decision;
        return decision;
    }

    Decision Decide(CommandMeta? command)
    {
        if (command is not {Available: true})
        {
            return new(false, null);
        }

        if (command.Policy is not { } policy)
        {
            return new(true, null);
        }

        if (!CommandPolicy.Allow(policy, command.ClrType, context))
        {
            return new(false, null);
        }

        return new(true, CommandPolicy.Rows(policy, command.ClrType, context));
    }

    readonly record struct Decision(bool Allowed, LambdaExpression? Rows);

    sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) :
        ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                return replacement;
            }

            return node;
        }
    }

    /// <summary>
    /// Rewrites every non-null scalar literal as the member read off a captured object that a closure
    /// would have produced, which the provider binds as a parameter. See <see cref="Parameterization"/>.
    /// </summary>
    sealed class ConstantParameterizer :
        ExpressionVisitor
    {
        public static ConstantParameterizer Instance { get; } = new();

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is null ||
                !Schema.IsScalar(node.Type))
            {
                return node;
            }

            return Parameterization.Parameterize(node.Value, node.Type);
        }
    }
}
