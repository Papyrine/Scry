namespace Scry;

/// <summary>
/// Executes a query request against a <see cref="DbContext"/>, applying validation, allow-list,
/// policies, and shaping. This is the programmatic entry point used by the HTTP endpoint and is also
/// usable directly (other transports, tests).
/// </summary>
public sealed class ScryProcessor
{
    QueryExecutor executor;
    Schema schema;
    ScryOptions options;

    internal ScryProcessor(Schema schema, ScryOptions options)
    {
        this.schema = schema;
        this.options = options;
        executor = new(schema, options);
    }

    /// <summary>Describes the allow-listed query surface for tooling (the query explorer).</summary>
    public ScryIntrospection Describe() => schema.Describe(options);

    /// <summary>
    /// Confirms the model's annotations match its live EF mapping (e.g. a <c>[Queryable]</c> type is
    /// really an entity, a <c>[QueryableComplex]</c> type is really a complex type), throwing a
    /// directed error otherwise. Called once at startup by <c>MapScry</c>; safe to call from other
    /// hosts that have a <see cref="DbContext"/>.
    /// </summary>
    public void ValidateAgainstModel(DbContext data) =>
        schema.ValidateAgainstModel(data.Model, options.ContextType);

    /// <summary>Builds a processor from configuration (e.g. for tests or non-DI hosting).</summary>
    public static ScryProcessor Create<TContext>(Action<ScryOptions> configure)
        where TContext : DbContext
    {
        var options = new ScryOptions(typeof(TContext));
        configure(options);
        return new(Schema.Build(options), options);
    }

    /// <summary>Validates and executes a request, returning the shaped result.</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data, IServiceProvider services) =>
        executor.Execute(request, data, services);

    /// <summary>Executes a request without a service provider (no DI-resolved policies).</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data) =>
        Execute(request, data, EmptyServiceProvider.Instance);
}