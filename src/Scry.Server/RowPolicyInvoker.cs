/// <summary>
/// The typed call that applies a row policy: <see cref="IReturnablePolicy{T}.Filter"/>, reached through
/// a class closed over the policy's entity type once, rather than a <see cref="MethodInfo"/> invoked
/// reflectively per policy per request — an argument array, an invoke stub, and a failure wrapped in a
/// <see cref="TargetInvocationException"/> each time. Stateless, so one serves every instance of every
/// policy over its type; and a failure arrives as the policy threw it, which is how a cached policy's
/// always has.
/// </summary>
interface IRowPolicyInvoker
{
    IQueryable Filter(object policy, IQueryable rows, ScryPolicyContext context);
}

static class RowPolicyInvoker
{
    public static IRowPolicyInvoker For(Type entityType) =>
        (IRowPolicyInvoker) Activator.CreateInstance(typeof(RowPolicyInvoker<>).MakeGenericType(entityType))!;
}

sealed class RowPolicyInvoker<T> :
    IRowPolicyInvoker
{
    public IQueryable Filter(object policy, IQueryable rows, ScryPolicyContext context) =>
        ((IReturnablePolicy<T>) policy).Filter((IQueryable<T>) rows, context);
}
