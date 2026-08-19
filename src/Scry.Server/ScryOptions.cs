namespace Scry;

/// <summary>
/// Configures the server-side query executor: which model to expose, in-memory POCO sources,
/// row-level policies, and resource limits enforced during validation.
/// </summary>
public sealed class ScryOptions(Type contextType)
{
    // begin-snippet: scryOptionsLimits
    /// <summary>Maximum number of rows a single query may request via <c>Take</c>. Default 1000.</summary>
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>
    /// Page size applied to a paged query (<c>ToPageAsync</c>) that does not request one. Bounds an
    /// otherwise-unbounded page; the effective size is always capped by <see cref="MaxPageSize"/>. Default 100.
    /// </summary>
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>Maximum navigation-path length allowed in a member expression. Default 4.</summary>
    public int MaxNavigationDepth { get; set; } = 4;

    /// <summary>Maximum number of operators in a query pipeline. Default 32.</summary>
    public int MaxPipelineLength { get; set; } = 32;

    /// <summary>Maximum expression nesting depth in a predicate. Default 32.</summary>
    public int MaxExpressionDepth { get; set; } = 32;

    /// <summary>
    /// Maximum number of values a client may supply to a set-membership test (<c>Contains</c>, which
    /// becomes a SQL <c>IN</c>). Default 1000.
    /// </summary>
    public int MaxInValues { get; set; } = 1000;

    /// <summary>
    /// Maximum number of queries one batch request may carry. Default 20.
    /// </summary>
    /// <remarks>
    /// A batch is the one place a single request costs more than one query, so this is the bound that
    /// keeps it from being an amplifier: every other limit is per query and would otherwise apply to an
    /// arbitrary number of them. A batch over the limit is rejected whole, before any entry runs.
    /// </remarks>
    public int MaxBatchSize { get; set; } = 20;

    /// <summary>
    /// Maximum number of rows a streamed query may return, or null — the default — for no limit.
    /// </summary>
    /// <remarks>
    /// Null matches <c>ToListAsync</c>, which has never been bounded either: <see cref="MaxPageSize"/>
    /// caps <c>Take</c> and a page, not an unbounded enumeration. Nor is streaming the safer of the two
    /// server-side any longer — a list that outgrows <see cref="ResponseSpillThreshold"/> is written out
    /// as it is read, so neither holds its rows. What both hold is a connection and a response open for
    /// as long as the client reads, which is the reason to offer a bound at all. A stream that
    /// reaches the limit ends with an error marker rather than a short result, so a client cannot
    /// mistake truncation for the end of the data.
    /// </remarks>
    public int? MaxStreamRows { get; set; }

    /// <summary>
    /// The longest encoded query this deployment wants asked as a URL. Default 4096; zero maps no GET
    /// route at all, so every query travels as a body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the limits above this one rejects nothing — it is advertised rather than enforced,
    /// because the ceiling it describes is not this server's. What actually truncates or refuses a long
    /// URL is whichever hop is strictest: 8 KB on a whole request line is the common default for a
    /// server or a proxy, and the number here is the budget a client is asked to stay inside of so it
    /// never finds out where the real edge is. A request that arrives is answered whatever its length.
    /// </para>
    /// <para>
    /// It is a deployment setting rather than something the model declares, since the ingress in front
    /// of a server is a property of where it runs — one model can be hosted behind two of them.
    /// Clients learn it from <see cref="WireFormat.UrlLimitHeader"/>, carried on every response.
    /// </para>
    /// <para>
    /// Zero is the exception, and is enforced: it says a query may never appear in a URL here, which is
    /// a statement about this deployment rather than a guess about a length. <c>MapScry</c> honours it
    /// by not mapping the GET route, so routing answers such a request with a 405 naming POST and Scry
    /// never sees it. Setting it means giving up conditional requests — see /docs/caching.md.
    /// </para>
    /// </remarks>
    public int QueryUrlLimit { get; set; } = QueryUrl.MaxLength;
    // end-snippet

    /// <summary>
    /// What the rows a query would return are current as of — a database change marker, typically.
    /// Null, the default, writes no <c>ETag</c> and answers nothing conditionally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set, a query asked as a URL is answered with an <c>ETag</c> over the schema stamp, this
    /// token, the query, and <see cref="CacheScope"/>; a client sending that value back as
    /// <c>If-None-Match</c> is answered <c>304</c> rather than re-executed. Returning null skips one
    /// request rather than turning the whole thing off, so a source that cannot answer right now
    /// degrades to a full response.
    /// </para>
    /// <para>
    /// A delegate rather than a built-in reader because "has anything changed" has no one answer: a
    /// transaction log position, a change-tracking version, a counter in Redis. Scry.Server.Delta
    /// supplies one for a <c>DbContext</c> in a line.
    /// </para>
    /// <para>
    /// The token invalidates every query at once — anything written moves it, so one write empties the
    /// whole cache. That is the right default and the reason this suits a read-heavy database and does
    /// not suit a write-heavy one.
    /// </para>
    /// </remarks>
    public Func<HttpContext, Cancel, ValueTask<string?>>? QueryFreshness { get; set; }

    /// <summary>
    /// Who a cached response belongs to. Anything a response varies by that its query does not
    /// describe: the tenant a row policy scopes rows to, the principal an attachment check answers
    /// for, a build id where a response shape can change without the queryable surface changing.
    /// </summary>
    /// <remarks>
    /// Without it, two callers asking the same question share an <c>ETag</c> — and a cache that holds
    /// one caller's rows will hand them to the next. A server with a row or attachment policy
    /// therefore has to set this before <see cref="QueryFreshness"/> is honoured; <c>MapScry</c>
    /// refuses to start otherwise, since the alternative is a leak that only shows up in production.
    /// </remarks>
    public Func<HttpContext, string?>? CacheScope { get; set; }

    /// <summary>
    /// The size in bytes past which a response stops being held whole and is sent as it is written.
    /// Default 65,536 (64 KB); zero holds every response whole, as every response once was.
    /// </summary>
    /// <remarks>
    /// This is not one of the limits above: crossing it rejects nothing and bounds nothing a client may
    /// ask for. It is the point at which an unbounded result stops being resident, and what it pays is
    /// the response's <c>Content-Length</c> — one that fits is sent whole and declares its length, and a
    /// failure part-way through one is still answered as a 400 or a 500 with a body. Past the threshold
    /// the status is long since committed, so a failure can only truncate the response; a truncated one
    /// is never mistakable for a complete one, because the host closes the connection without a
    /// terminating chunk rather than synthesising a valid end. A result that carries
    /// <c>[BinaryTransfer]</c> values is held whole whatever this says, since its raw parts have to
    /// precede the JSON document that references them.
    /// </remarks>
    public int ResponseSpillThreshold { get; set; } = 64 * 1024;

    /// <summary>
    /// The collation applied when a client asks for a case-sensitive string comparison. Null — the
    /// default — rejects such a request instead, so the feature is opt-in per deployment.
    /// </summary>
    /// <remarks>
    /// This is deliberately a server setting rather than something a request carries. A collation
    /// cannot be a query parameter: it is emitted into the SQL text, so accepting one from a client
    /// would be the one place an attacker-supplied string reached SQL as anything but a parameter.
    /// Naming it here keeps the request to an intent — case-sensitive or not — and the SQL-affecting
    /// string under the server's control. Set it to a collation the database actually has, e.g.
    /// <c>Latin1_General_CS_AS</c> on SQL Server.
    /// </remarks>
    public string? CaseSensitiveCollation { get; set; }

    /// <summary>
    /// The collation applied when a client asks for a case-insensitive string comparison. Null — the
    /// default — rejects such a request. See <see cref="CaseSensitiveCollation"/> for why this is
    /// configured rather than requested; e.g. <c>Latin1_General_CI_AS</c> on SQL Server.
    /// </summary>
    public string? CaseInsensitiveCollation { get; set; }

    /// <summary>
    /// HMAC key used to sign keyset paging cursors. When null a random per-process key is used, so
    /// cursors do not survive a restart or work across multiple instances — set a stable key for a
    /// scaled-out or restart-tolerant deployment. Signing enforces the opaque-cursor contract; it is
    /// not an authorization control (a decoded cursor is always re-validated and policy-filtered).
    /// </summary>
    public byte[]? CursorSigningKey { get; set; }

    /// <summary>
    /// Whether startup translates each navigation that steps into a row-policied source, to prove the
    /// policy composes where it is applied. On by default: the alternative to failing here is failing
    /// as a generic 500 on the first client to name such a member.
    /// </summary>
    /// <remarks>
    /// Probing resolves and runs every such policy once, outside a request — with no principal, and
    /// with empty headers. Clear this where a policy cannot answer under those conditions; the policy
    /// still applies per request either way, and only the startup proof is given up.
    /// </remarks>
    public bool ProbePoliciedNavigations { get; set; } = true;

    internal Type ContextType { get; private set; } = contextType;

    internal Dictionary<Type, Func<IServiceProvider, IQueryable>> PocoSources { get; } = [];

    internal Dictionary<Type, Type> Policies { get; } = [];

    /// <summary>Registers the data for a <c>[QueryablePoco]</c> source, resolved per request.</summary>
    public void AddPocoSource<T>(Func<IServiceProvider, IEnumerable<T>> factory)
        where T : class =>
        PocoSources[typeof(T)] = services => factory(services).AsQueryable();

    /// <summary>Registers a fixed in-memory <c>[QueryablePoco]</c> source.</summary>
    public void AddPocoSource<T>(IEnumerable<T> items)
        where T : class =>
        AddPocoSource(_ => items);

    /// <summary>
    /// Attaches a row/instance policy to an entity, replacing any <c>[ReturnableWith]</c> on that same
    /// type. Like the attribute it also covers every opted-in type deriving from that one, whose own
    /// policies narrow further rather than replace this one.
    /// </summary>
    public void AddPolicy<TEntity, TPolicy>()
        where TPolicy : IReturnablePolicy<TEntity> =>
        Policies[typeof(TEntity)] = typeof(TPolicy);

    internal Dictionary<Type, Type> AttachmentPolicies { get; } = [];

    /// <summary>
    /// Attaches the authorization check for an entity's <c>[Attachment]</c> members, replacing any
    /// <c>[AttachmentWith]</c> on that same type. A type exposing an attachment must have one, here or
    /// as the attribute, or the server refuses to start.
    /// </summary>
    public void AddAttachmentPolicy<TEntity, TPolicy>()
        where TPolicy : IAttachmentPolicy<TEntity> =>
        AttachmentPolicies[typeof(TEntity)] = typeof(TPolicy);
}
