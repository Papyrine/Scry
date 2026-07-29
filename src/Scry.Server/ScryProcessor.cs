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
    /// A hash of this server's allow-listed surface. Advertised on every response so a client can
    /// compare it against the stamp it was generated with and detect a drifted model.
    /// </summary>
    public string SchemaStamp => schema.Stamp;

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
    public QueryResponse Execute(QueryRequest request, DbContext data, IServiceProvider services)
    {
        var drifted = request.Stamp is { } requestStamp && requestStamp != schema.Stamp;
        try
        {
            var response = executor.Execute(request, data, services) with
            {
                // Carried on every response, not only a drifted one: this is the signal a client uses
                // to notice drift in the first place, and it is the only such channel for a transport
                // that is not HTTP (which also advertises it as a response header).
                Stamp = schema.Stamp
            };

            // A drifted client may have been generated before an enum value rename, in which case the
            // payload carries names it does not know. The aliases let its reader resolve them; a
            // matching (or absent) stamp proves the client already has the current names, so nothing
            // rides along in the common case.
            if (drifted && schema.EnumAliases.Count > 0)
            {
                response = response with { EnumAliases = schema.EnumAliases };
            }

            return response;
        }
        // A rejected query from a client that was generated against a different model surface is far
        // more likely stale than hostile; say so, instead of leaving an unexplained rejection. A
        // matching stamp (or none) reports the plain validation message.
        catch (ScryValidationException exception) when (drifted)
        {
            throw new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                StaleClient = true
            };
        }
    }

    /// <summary>Executes a request without a service provider (no DI-resolved policies).</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data) =>
        Execute(request, data, EmptyServiceProvider.Instance);
}