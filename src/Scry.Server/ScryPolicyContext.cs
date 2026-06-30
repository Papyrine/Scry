namespace Scry;

/// <summary>Context handed to an <see cref="IReturnablePolicy{T}"/>, exposing the request services.</summary>
public sealed class ScryPolicyContext(IServiceProvider services, DbContext db)
{
    /// <summary>The request-scoped service provider (e.g. for the current user/tenant).</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The active <see cref="DbContext"/>.</summary>
    public DbContext Db { get; } = db;
}