using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// Translates, once at startup, every navigation that steps into a row-policied source.
/// </summary>
/// <remarks>
/// Applying a policy at a traversal (<see cref="NavigationPolicy"/>) puts the policy's own queryable
/// in correlated-subquery position, where a policy that composes perfectly well as a root filter can
/// still fail to translate — a client-evaluated predicate, a materializing call. Left undiscovered
/// that surfaces as a generic 500 on whichever client first names the member; found here it is a
/// startup failure naming the policy and the navigation that reaches it.
/// </remarks>
static class NavigationPolicyProbe
{
    public static void Run(Schema schema, IModel model, Func<string, Func<PolicyUse, bool>?, IQueryable> sources, DbContext db)
    {
        // Denials are not probed here: this asks whether each rewrite translates, and running the
        // reporting query as well would make startup depend on what the data happens to hold.
        var navigations = new NavigationPolicy(schema, model, sources);
        foreach (var meta in schema.Types)
        {
            // Only real entities own a navigation EF can key on. A complex type reaching a source is
            // left to fail per-query, where the message can name the path that did it.
            if (model.FindEntityType(meta.ClrType) is null)
            {
                continue;
            }

            foreach (var member in meta.Members.Values)
            {
                // A collection of a policied element is read through the same rewrite, and one that
                // reached here was legalized by its policy — so it needs the same proof.
                var target = member.Kind switch
                {
                    MemberKind.Navigation => Nullable.GetUnderlyingType(member.Type) ?? member.Type,
                    MemberKind.Collection => Schema.CollectionElement(member.Type),
                    _ => null
                };

                if (target is not null &&
                    navigations.Applies(target))
                {
                    Probe(schema, navigations, db, meta.ClrType, member, target, member.Kind);
                }
            }
        }
    }

    static void Probe(Schema schema, NavigationPolicy navigations, DbContext db, Type ownerType, Member member, Type target, MemberKind kind)
    {
        var owner = Expression.Parameter(ownerType, "o");

        // Running the policy is itself part of what is being probed: it is resolved and invoked here
        // exactly as a request would, so a policy that cannot even be built is a startup failure.
        Expression correlated;
        try
        {
            correlated = kind == MemberKind.Collection
                ? navigations.CorrelateMany(owner, ownerType, member, target)
                : navigations.Correlate(owner, ownerType, member, target);
        }
        catch (Exception exception)
        {
            throw new(
                $"The row policy on '{target.Name}' could not be applied to '{ownerType.Name}.{member.Name}', which navigates into it. {exception.Message}",
                exception);
        }

        var query = SetOf(db, ownerType);
        var projected = Project(schema, query, owner, correlated, target, kind);

        try
        {
            projected.ToQueryString();
        }
        catch (Exception exception)
        {
            throw new(
                $"""
                 The row policy on '{target.Name}' does not translate where '{ownerType.Name}.{member.Name}' navigates into it. A navigation into a policied source is read through that source's policy, which puts the policy's queryable in a correlated subquery — so it has to be composable, not merely runnable at the root. {exception.Message}

                 Rewrite the policy so it is a filter the provider can translate, stop exposing '{ownerType.Name}.{member.Name}', or drop the policy on '{target.Name}'.
                 """,
                exception);
        }
    }

    /// <summary>
    /// The query the probe translates: a scalar read through the traversal, which is the shape a
    /// request produces. Projecting the row itself would translate differently and prove less.
    /// </summary>
    static IQueryable Project(Schema schema, IQueryable query, ParameterExpression owner, Expression correlated, Type target, MemberKind kind)
    {
        // A collection is only ever aggregated, so counting it is the shape a request produces — and
        // the one that puts the rewritten subquery where a request would put it.
        var leaf = kind == MemberKind.Collection
            ? Expression.Call(Count.MakeGenericMethod(target), correlated)
            : Leaf(schema, correlated, target);

        return query.Provider.CreateQuery(
            Expression.Call(
                typeof(Queryable),
                nameof(Queryable.Select),
                [owner.Type, leaf.Type],
                query.Expression,
                Expression.Quote(Expression.Lambda(leaf, owner))));
    }

    /// <summary>
    /// The scalar the probe reads through the traversal, or the row itself where the target exposes
    /// none. Widened for the same reason a request's leaf is: the policy can return no row.
    /// </summary>
    static Expression Leaf(Schema schema, Expression correlated, Type target)
    {
        if (!schema.TryGetType(target, out var meta) ||
            meta.Members.Values.FirstOrDefault(_ => _.Kind == MemberKind.Scalar) is not { } scalar)
        {
            return correlated;
        }

        var leaf = (Expression)Expression.Property(correlated, scalar.Property);
        if (leaf.Type.IsValueType &&
            Nullable.GetUnderlyingType(leaf.Type) is null)
        {
            leaf = Expression.Convert(leaf, typeof(Nullable<>).MakeGenericType(leaf.Type));
        }

        return leaf;
    }

    static IQueryable SetOf(DbContext db, Type entityType) =>
        (IQueryable)Set.MakeGenericMethod(entityType).Invoke(db, [])!;

    static readonly MethodInfo Count = typeof(Queryable)
        .GetMethods()
        .Single(_ => _.Name == nameof(Queryable.Count) && _.GetParameters().Length == 1);

    static readonly MethodInfo Set = typeof(DbContext)
        .GetMethods()
        .Single(_ => _.Name == nameof(DbContext.Set) &&
                     _.IsGenericMethodDefinition &&
                     _.GetParameters().Length == 0);
}
