namespace Scry;

/// <summary>
/// A server-side row/instance policy applied to a queryable source <em>before</em> any client
/// predicate, so client filters can only narrow the already-authorized set (tenant scoping,
/// soft-delete, row security). Register via <see cref="ScryOptions.AddPolicy{TEntity,TPolicy}()"/>
/// or the <c>[ReturnableWith]</c> attribute.
/// </summary>
// begin-snippet: returnablePolicyInterface
public interface IReturnablePolicy<T>
{
    IQueryable<T> Filter(IQueryable<T> source, ScryPolicyContext context);
}
// end-snippet