using Microsoft.EntityFrameworkCore;
using Pneumatic.Wire;

namespace Pneumatic;

/// <summary>
/// Executes a query request against a <see cref="DbContext"/>, applying validation, allow-list,
/// policies, and shaping. This is the programmatic entry point used by the HTTP endpoint and is also
/// usable directly (other transports, tests).
/// </summary>
public sealed class PneumaticProcessor
{
    readonly QueryExecutor executor;

    internal PneumaticProcessor(PneumaticSchema schema, PneumaticOptions options) =>
        executor = new(schema, options);

    /// <summary>Builds a processor from configuration (e.g. for tests or non-DI hosting).</summary>
    public static PneumaticProcessor Create(Action<PneumaticOptions> configure)
    {
        var options = new PneumaticOptions();
        configure(options);
        return new(PneumaticSchema.Build(options), options);
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
