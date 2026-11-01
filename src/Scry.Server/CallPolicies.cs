/// <summary>
/// What applying a call's policies makes, kept for the call: the context every policy is handed, and
/// each policy that neither the host supplied nor its services resolve. A query applies its policies
/// at several sites — its root, each navigation, each side source — and each made both afresh.
/// </summary>
/// <remarks>
/// One call applies its policies on one thread at a time, as it uses its one DbContext, so nothing
/// here is locked.
/// </remarks>
sealed class CallPolicies
{
    ScryPolicyContext? context;
    Dictionary<Type, object>? created;

    /// <summary>The context for a call's policies, made once for each DbContext it is asked with.</summary>
    public ScryPolicyContext Context(CallScope scope, DbContext db)
    {
        if (context is null ||
            context.Db != db)
        {
            context = new(scope.Services, db, scope.RequestHeaders, scope.ResponseHeaders);
        }

        return context;
    }

    /// <summary>A policy made with its parameterless constructor, made once for the call.</summary>
    public object? Created(Type policy)
    {
        created ??= [];
        if (created.TryGetValue(policy, out var made))
        {
            return made;
        }

        made = Activator.CreateInstance(policy);
        if (made is not null)
        {
            created[policy] = made;
        }

        return made;
    }
}
