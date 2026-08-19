using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// Applies a source's row policy where a query traverses <em>into</em> that source through a
/// navigation. A policy filters a source and a navigation is not one, so reading a member off the
/// navigation directly would hand over exactly the rows the policy exists to hide — and a predicate
/// over such a member is an oracle for them even when nothing is projected.
/// </summary>
/// <remarks>
/// The traversal is rewritten into a correlated subquery over the policy-filtered set, keyed on the
/// navigation's own foreign key. A row the policy hides matches nothing, so the traversal yields null
/// — indistinguishable from an absent optional navigation, which is the point: the client learns that
/// there is nothing here to read, not that there is something it may not have.
/// </remarks>
sealed class NavigationPolicy(Schema schema, IModel model, Func<string, IQueryable> sources)
{
    /// <summary>Whether stepping into <paramref name="target"/> means stepping into a policied source.</summary>
    public bool Applies(Type target) =>
        schema.TryGetPoliciedSource(target, out _);

    /// <summary>
    /// The expression a traversal of <paramref name="navigation"/> off <paramref name="owner"/>
    /// resolves to: the first row of the policy-filtered target source whose key matches the owner's,
    /// or null where the policy allows none.
    /// </summary>
    public Expression Correlate(Expression owner, Type ownerType, Member navigation, Type target)
    {
        if (!schema.TryGetPoliciedSource(target, out var source))
        {
            throw new($"'{target.Name}' carries no row policy, so a traversal into it needs no rewrite.");
        }

        var filtered = sources(source.Name);
        var row = Expression.Parameter(target, "p");
        var predicate = Expression.Lambda(
            KeyMatch(row, owner, ownerType, navigation, target),
            row);

        return Expression.Call(
            FirstOrDefault.MakeGenericMethod(target),
            filtered.Expression,
            Expression.Quote(predicate));
    }

    /// <summary>
    /// The correlation between the policy-filtered row and the owner it was reached from: the
    /// navigation's foreign key, read from the live EF model rather than guessed from names. Composite
    /// keys compare pairwise, and a key nullable on one side only is lifted so the two sides agree.
    /// </summary>
    Expression KeyMatch(Expression row, Expression owner, Type ownerType, Member navigation, Type target)
    {
        var entityType = model.FindEntityType(ownerType) ??
                         throw new($"'{ownerType.Name}' is not an entity type in the model, so the navigation '{navigation.Name}' into policied '{target.Name}' has no foreign key to correlate on. A policied source reached this way cannot be filtered; remove the policy, or stop exposing the navigation.");

        var found = entityType.FindNavigation(navigation.Name) ??
                    throw new($"'{ownerType.Name}.{navigation.Name}' is not a navigation in the model, so the traversal into policied '{target.Name}' has no foreign key to correlate on.");

        var key = found.ForeignKey;

        // On the dependent the owner holds the foreign key and the target holds the principal key; on
        // the principal it is the other way round. Either way the pairing is positional.
        var (ownerKeys, targetKeys) = found.IsOnDependent
            ? (key.Properties, key.PrincipalKey.Properties)
            : (key.PrincipalKey.Properties, key.Properties);

        Expression? match = null;
        for (var i = 0; i < ownerKeys.Count; i++)
        {
            var ownerSide = Property(owner, ownerKeys[i], ownerType);
            var targetSide = Property(row, targetKeys[i], target);
            var comparison = Expression.Equal(Lift(ownerSide, targetSide.Type), Lift(targetSide, ownerSide.Type));
            match = match is null ? comparison : Expression.AndAlso(match, comparison);
        }

        return match ??
               throw new($"'{ownerType.Name}.{navigation.Name}' has no foreign key properties to correlate the policied '{target.Name}' on.");
    }

    /// <summary>
    /// Reads a key property off a row. A shadow property has no CLR member to read, so a navigation
    /// keyed on one is refused rather than correlated on something else.
    /// </summary>
    static Expression Property(Expression row, IProperty property, Type owner)
    {
        var info = property.PropertyInfo ??
                   throw new($"'{owner.Name}.{property.Name}' is a shadow property, so a navigation keyed on it cannot be correlated to a policied source. Map the key to a CLR property, or drop the policy.");

        return Expression.Property(row, info);
    }

    /// <summary>
    /// Widens one side of a key comparison where the other is nullable, so an optional foreign key and
    /// the non-nullable primary key it points at still compare.
    /// </summary>
    static Expression Lift(Expression value, Type other) =>
        Nullable.GetUnderlyingType(other) is not null &&
        value.Type.IsValueType &&
        Nullable.GetUnderlyingType(value.Type) is null
            ? Expression.Convert(value, typeof(Nullable<>).MakeGenericType(value.Type))
            : value;

    // The predicate overload specifically: the other two-parameter one takes a default value, which
    // would bind a row rather than filter to one.
    static readonly MethodInfo FirstOrDefault = typeof(Queryable)
        .GetMethods()
        .Single(_ => _.Name == nameof(Queryable.FirstOrDefault) &&
                     _.GetParameters() is {Length: 2} parameters &&
                     parameters[1].ParameterType.IsGenericType &&
                     parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));
}
