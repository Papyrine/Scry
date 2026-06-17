using Microsoft.EntityFrameworkCore;

namespace Scry;

/// <summary>
/// Executes a query request against a <see cref="DbContext"/>, applying validation, allow-list,
/// policies, and shaping. This is the programmatic entry point used by the HTTP endpoint and is also
/// usable directly (other transports, tests).
/// </summary>
public sealed class ScryProcessor
{
    readonly QueryExecutor executor;

    internal ScryProcessor(ScrySchema schema, ScryOptions options) =>
        executor = new(schema, options);

    /// <summary>Builds a processor from configuration (e.g. for tests or non-DI hosting).</summary>
    public static ScryProcessor Create(Action<ScryOptions> configure)
    {
        var options = new ScryOptions();
        configure(options);
        return new(ScrySchema.Build(options), options);
    }

    /// <summary>Validates and executes a request, returning the shaped result.</summary>
    public QueryResponse Execute(QueryRequest request, DbContext db, IServiceProvider services) =>
        executor.Execute(request, db, services);

    /// <summary>Executes a request without a service provider (no DI-resolved policies).</summary>
    public QueryResponse Execute(QueryRequest request, DbContext db) =>
        Execute(request, db, EmptyServiceProvider.Instance);
}

sealed class EmptyServiceProvider :
    IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();

    public object? GetService(Type serviceType) => null;
}
