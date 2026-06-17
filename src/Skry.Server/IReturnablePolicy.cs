using Microsoft.EntityFrameworkCore;

namespace Skry;

/// <summary>
/// A server-side row/instance policy applied to a queryable source <em>before</em> any client
/// predicate, so client filters can only narrow the already-authorized set (tenant scoping,
/// soft-delete, row security). Register via <see cref="SkryOptions.AddPolicy{TEntity,TPolicy}"/>
/// or the <c>[ReturnableWith]</c> attribute.
/// </summary>
public interface IReturnablePolicy<T>
{
    IQueryable<T> Filter(IQueryable<T> source, SkryPolicyContext context);
}

/// <summary>Context handed to an <see cref="IReturnablePolicy{T}"/>, exposing the request services.</summary>
public sealed class SkryPolicyContext(IServiceProvider services, DbContext db)
{
    /// <summary>The request-scoped service provider (e.g. for the current user/tenant).</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The active <see cref="DbContext"/>.</summary>
    public DbContext Db { get; } = db;
}
