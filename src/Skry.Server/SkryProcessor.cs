using Microsoft.EntityFrameworkCore;
using Skry.Wire;

namespace Skry;

/// <summary>
/// Executes a query request against a <see cref="DbContext"/>, applying validation, allow-list,
/// policies, and shaping. This is the programmatic entry point used by the HTTP endpoint and is also
/// usable directly (other transports, tests).
/// </summary>
public sealed class SkryProcessor
{
    readonly QueryExecutor executor;

    internal SkryProcessor(SkrySchema schema, SkryOptions options) =>
        executor = new(schema, options);

    /// <summary>Builds a processor from configuration (e.g. for tests or non-DI hosting).</summary>
    public static SkryProcessor Create(Action<SkryOptions> configure)
    {
        var options = new SkryOptions();
        configure(options);
        return new(SkrySchema.Build(options), options);
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
