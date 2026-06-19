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
    readonly ScrySchema schema;
    readonly ScryOptions options;

    internal ScryProcessor(ScrySchema schema, ScryOptions options)
    {
        this.schema = schema;
        this.options = options;
        executor = new(schema, options);
    }

    /// <summary>Describes the allow-listed query surface for tooling (the query explorer).</summary>
    public ScryIntrospection Describe() => schema.Describe(options);

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
