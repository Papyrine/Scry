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
/// there is nothing here to read, not that there is something it may not have. A collection navigation
/// is rewritten the same way and yields the elements the policy allows.
/// <para>
/// Where the policy is configured to fail the request instead, the rewrite is unchanged and a probe
/// is planned alongside it — see <see cref="Deny"/>.
/// </para>
/// </remarks>
/// <param name="probes">
/// Where a traversal's probe is planned, for the executor to ask before the query runs — or null
/// where nothing will run, which is a SQL preview and the startup translation check.
/// </param>
sealed class NavigationPolicy(
    Schema schema,
    IModel model,
    Func<string, Func<PolicyUse, bool>?, IQueryable> sources,
    List<DeniedRowProbe>? probes = null)
{
    // One answer per traversal, however many times a query reads through it: the question is about the
    // relationship, and a request naming the same member in a filter, an ordering and a projection
    // would otherwise ask the database the same thing three times.
    readonly HashSet<(Type Owner, string Member)> probed = [];

    // The policy-filtered target, resolved once per source a query traverses into: the chain is the
    // same at every site that reads through it, and resolving is running that chain.
    readonly Dictionary<Type, IQueryable> filtered = [];

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
        var filtered = Filtered(target, include: null);
        Deny(ownerType, navigation, target, DeniedPosition.Navigation);

        var row = Expression.Parameter(target, "p");
        var predicate = Expression.Lambda(
            KeyMatch(row, owner, ownerType, navigation, target),
            row);

        return Expression.Call(
            firstOrDefault.MakeGenericMethod(target),
            filtered.Expression,
            Expression.Quote(predicate));
    }

    /// <summary>
    /// The same for a collection navigation: every row of the policy-filtered target source keyed to
    /// this owner. An aggregate over it counts what a direct query of that source would have returned,
    /// which is what makes exposing the collection safe at all.
    /// </summary>
    public Expression CorrelateMany(Expression owner, Type ownerType, Member navigation, Type element)
    {
        var filtered = Filtered(element, include: null);
        Deny(ownerType, navigation, element, DeniedPosition.CollectionNavigation);

        var row = Expression.Parameter(element, "p");
        var predicate = Expression.Lambda(
            KeyMatch(row, owner, ownerType, navigation, element),
            row);

        return Expression.Call(
            where.MakeGenericMethod(element),
            filtered.Expression,
            Expression.Quote(predicate));
    }

    /// <summary>
    /// Fails the request where the target's policy denies a row reachable through this navigation and
    /// says a denial should be reported rather than hidden.
    /// </summary>
    /// <remarks>
    /// Asked of the relationship rather than of one query's rows: the owner rows are the whole owner
    /// source, policy-filtered, not the ones this query's operators would have kept. A navigation is
    /// read per row and which rows those are depends on the whole shape of the query — including
    /// filters written over this very traversal — so narrowing by them would make the answer depend on
    /// the question in a way a caller could use to probe. Erring wide costs a request that would have
    /// succeeded; erring narrow returns a row the host asked to be told about.
    /// </remarks>
    void Deny(Type ownerType, Member navigation, Type target, DeniedPosition position)
    {
        if (probes is null ||
            !schema.TryGetPoliciedSource(target, out var source) ||
            !source.Policies.Any(_ => _.Errors(position)) ||
            !probed.Add((ownerType, navigation.Name)) ||
            !schema.TryGetSourceForType(ownerType, out var owners))
        {
            return;
        }

        // The owner rows this caller can see at all: its own policies apply in full, so a denial is
        // never reported for a traversal off a row that was never readable.
        var query = sources(owners.Name, null);
        var hide = Filtered(target, _ => !_.Errors(position));
        var full = Filtered(target, null);

        var owner = Expression.Parameter(ownerType, "o");
        var denied = Expression.GreaterThan(
            Reachable(hide, owner, ownerType, navigation, target),
            Reachable(full, owner, ownerType, navigation, target));

        probes.Add(DeniedRowProbe.Owners(query, Expression.Lambda(denied, owner)));
    }

    /// <summary>
    /// How many rows of <paramref name="target"/> this owner reaches. Counting rather than comparing
    /// keys covers both kinds of navigation with one shape — a reference reaches nought or one — and
    /// asks nothing of the target's own key, which may be one EF holds in shadow.
    /// </summary>
    Expression Reachable(IQueryable target, Expression owner, Type ownerType, Member navigation, Type element)
    {
        var row = Expression.Parameter(element, "p");
        return Expression.Call(
            countWithPredicate.MakeGenericMethod(element),
            target.Expression,
            Expression.Quote(Expression.Lambda(KeyMatch(row, owner, ownerType, navigation, element), row)));
    }

    IQueryable Filtered(Type target, Func<PolicyUse, bool>? include)
    {
        if (include is null &&
            filtered.TryGetValue(target, out var whole))
        {
            return whole;
        }

        if (!schema.TryGetPoliciedSource(target, out var source))
        {
            throw new($"'{target.Name}' carries no row policy, so a traversal into it needs no rewrite.");
        }

        var resolved = sources(source.Name, include);
        if (include is null)
        {
            filtered[target] = resolved;
        }

        return resolved;
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
        // the principal it is the other way round — which is also every collection navigation. Either
        // way the pairing is positional.
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
    static readonly MethodInfo firstOrDefault = typeof(Queryable)
        .GetMethods()
        .Single(_ => _.Name == nameof(Queryable.FirstOrDefault) &&
                     _.GetParameters() is [_, {ParameterType.IsGenericType: true}] parameters &&
                     parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));

    // The row-predicate overloads. Where also has an indexed one, whose predicate takes the row and its
    // position, so the arity of the delegate is what tells the two apart rather than the parameter count.
    static readonly MethodInfo where = RowPredicate(nameof(Queryable.Where));
    static readonly MethodInfo countWithPredicate = RowPredicate(nameof(Queryable.Count));

    static MethodInfo RowPredicate(string name) =>
        typeof(Queryable).GetMethods()
            .Single(_ => _.Name == name &&
                         _.GetParameters() is [_, {ParameterType.IsGenericType: true}] parameters &&
                         parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>) &&
                         parameters[1].ParameterType.GenericTypeArguments[0].GenericTypeArguments.Length == 2);
}
