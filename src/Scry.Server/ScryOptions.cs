using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>
    /// Maximum number of operators in a query pipeline. The pipeline a join's inner side or a set
    /// operand carries is bounded by the same number. Default 32.
    /// </summary>
    public int MaxPipelineLength { get; set; } = 32;

    /// <summary>Maximum expression nesting depth in a predicate. Default 32.</summary>
    public int MaxExpressionDepth { get; set; } = 32;

    /// <summary>
    /// Maximum number of expression nodes one request may carry, counted across every predicate,
    /// key, selector, and projection in it — the pipeline's own, a join's or set operand's, and
    /// what a subquery or aggregate reads. Default 4096.
    /// </summary>
    /// <remarks>
    /// Depth bounds how deeply an expression nests and width how many members a projection names;
    /// neither bounds a flat chain of thousands of comparisons in one predicate, which is one
    /// statement the provider has to compile. The count is shape exactly as the pipeline length is.
    /// </remarks>
    public int MaxExpressionNodes { get; set; } = 4096;

    /// <summary>
    /// Maximum number of correlated subqueries one request may carry: every question about a
    /// collection and every membership test against another source, wherever it appears. Default 64.
    /// </summary>
    /// <remarks>
    /// Each is a query the database runs per row. Nesting one inside another is refused outright;
    /// this bounds how many may sit side by side.
    /// </remarks>
    public int MaxCorrelatedSubqueries { get; set; } = 64;

    /// <summary>
    /// Maximum number of members a projection may name, nested members included, and the same for
    /// the members a join projects. Default 256.
    /// </summary>
    /// <remarks>
    /// Every member is an expression the provider compiles and a column the query returns, so the
    /// width of a projection is work a request asks for, exactly as the length of its pipeline is. A
    /// query writing no <c>Select</c> is unaffected: its projection is the model's own members.
    /// </remarks>
    public int MaxProjectionMembers { get; set; } = 256;

    /// <summary>
    /// Maximum number of values a client may supply to a set-membership test (<c>Contains</c>, which
    /// becomes a SQL <c>IN</c>). Default 1000.
    /// </summary>
    public int MaxInValues { get; set; } = 1000;

    /// <summary>
    /// Maximum number of queries one batch request may carry. Default 20.
    /// </summary>
    /// <remarks>
    /// A batch is a single request that costs more than one query, so this is the bound that keeps it
    /// from being an amplifier: every other limit here is per query and would otherwise apply to an
    /// arbitrary number of them. A batch over the limit is rejected whole, before any entry runs. The
    /// other such request is a live query, which has bounds of its own —
    /// <see cref="MaxSubscriptions"/> and the options beside it.
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

    /// <summary>
    /// Reports a query that used at least this fraction of a limit — <c>0.8</c> for eight tenths —
    /// to every registered <see cref="IScryAuditor"/>, as
    /// <see cref="ScryAuditEntry.ApproachedLimits"/>. Rejects nothing. Null, the default, reports
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A limit can only be tightened once it is known how close real traffic runs to it, and a limit
    /// that does nothing but reject never says: the queries that stayed inside it are exactly the
    /// ones it leaves no trace of. Set this, watch for a while, then tighten on what came back.
    /// </para>
    /// <para>
    /// It covers <see cref="MaxPipelineLength" />, <see cref="MaxExpressionNodes" />,
    /// <see cref="MaxCorrelatedSubqueries" />, <see cref="MaxPageSize" />,
    /// <see cref="MaxNavigationDepth" />, <see cref="MaxProjectionMembers" /> and
    /// <see cref="MaxInValues" />. Two are left out. <see cref="MaxBatchSize" />, because a batch that
    /// stays inside it is audited per entry rather than as a batch, so there is no entry of its own to
    /// report it on. And <see cref="MaxExpressionDepth" />, because what the validator compares is how
    /// many times it recursed rather than how deeply the request nests — a number this could only
    /// mirror by repeating the shape of that walk, and would then misreport the day the two drifted.
    /// </para>
    /// <para>
    /// It costs one extra walk of the request, paid only where it is set, only once an auditor is
    /// registered to read the result, and only for a query that was not rejected — a refused request
    /// is never measured, so this cannot be used to make refusing cost more than it does.
    /// </para>
    /// </remarks>
    public double? LimitWatchFraction { get; set; }
    // end-snippet

    // begin-snippet: scryOptionsSubscriptions
    /// <summary>
    /// How many live queries this server holds open at once. Default zero, which maps no subscribe
    /// route at all: a live query is a connection held and a query re-run on other people's writes, so
    /// a deployment has one because it asked for one.
    /// </summary>
    /// <remarks>
    /// One past the limit is answered <c>503</c> with a <c>Retry-After</c>, and nothing about it runs.
    /// </remarks>
    public int MaxSubscriptions { get; set; }

    /// <summary>
    /// How many of those one caller may hold, where <see cref="Caller"/> can say who is asking.
    /// Default 20. One past it is answered <c>429</c>.
    /// </summary>
    public int MaxSubscriptionsPerCaller { get; set; } = 20;

    /// <summary>
    /// Who is asking: what a live query and a pending command are counted against, what a command is
    /// handed as its caller and audited under, and whose a pending command's outcome is. The
    /// authenticated name by default; null — an anonymous caller — is counted against nobody, so only
    /// the server-wide limits bound it.
    /// </summary>
    /// <remarks>
    /// Read from the authenticated principal or something derived from it, never from a header: a
    /// caller that names itself names somebody new each time, is bounded by nothing, and could claim
    /// somebody else's command.
    /// </remarks>
    public Func<HttpContext, string?> Caller { get; set; } = _ => _.User.Identity?.Name;

    /// <summary>
    /// The largest answer a live query may hold, in bytes. Default 1,048,576 (1 MB). An answer is
    /// written whole before it is compared with the one before it, so this is what a subscription can
    /// cost in memory — and one that outgrows it ends with a rejection saying so.
    /// </summary>
    public int MaxSubscriptionBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// How many live queries may be running against the database at once, across every subscription.
    /// Default 8. One write can make thousands of them due in the same instant; this is what turns
    /// that into a queue.
    /// </summary>
    public int MaxConcurrentSubscriptionRuns { get; set; } = 8;

    /// <summary>
    /// The least time between two runs of one live query. Default one second. Changes arriving inside
    /// it are not lost and not queued: the query runs once when the time is up and answers for all of
    /// them.
    /// </summary>
    public TimeSpan SubscriptionThrottle { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a live query is run whether or not anything reported a change. Default thirty
    /// seconds; null runs it only when told.
    /// </summary>
    /// <remarks>
    /// Reports make a live query fast; this makes it correct. It is what catches everything nothing
    /// reports — a bulk update nobody called <c>Notify</c> for, a write from another system, a policy
    /// that answers by a claim or the clock, a row a view derives from a table this query never names.
    /// A run that finds the answer unchanged sends nothing, so an idle poll costs a query and no
    /// bandwidth.
    /// </remarks>
    public TimeSpan? SubscriptionPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often an idle live query is sent a heartbeat. Default fifteen seconds.</summary>
    public TimeSpan SubscriptionHeartbeat { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long one live query's connection may last before the server ends it and the client asks
    /// again. Default thirty minutes; null ends it only when the authentication ticket expires.
    /// </summary>
    /// <remarks>
    /// Authorization is decided once per request, and a live query is one request. Ending it is what
    /// makes a caller prove who they are again — so this, or the ticket's expiry where that is sooner,
    /// bounds how long a revoked caller keeps receiving answers.
    /// </remarks>
    public TimeSpan? SubscriptionLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Something that moves whenever the database is written — a change marker, a log position. Asked
    /// every <see cref="ChangeProbeInterval"/> while any live query is open, from a service scope of
    /// its own; when the answer differs from the last one, every live query is run again. Null, the
    /// default, asks nothing.
    /// </summary>
    /// <remarks>
    /// This sees every writer there is — another node, another system, raw SQL — without any of them
    /// knowing Scry exists, which makes the database the backplane. What it cannot say is which
    /// entities changed, so it re-runs everything; a run that finds nothing new still sends nothing.
    /// Returning null skips one probe. Scry.Server.Delta supplies one for a <c>DbContext</c> in a line.
    /// </remarks>
    public Func<IServiceProvider, Cancel, ValueTask<string?>>? ChangeProbe { get; set; }

    /// <summary>How often <see cref="ChangeProbe"/> is asked. Default one second.</summary>
    public TimeSpan ChangeProbeInterval { get; set; } = TimeSpan.FromSeconds(1);
    // end-snippet

    // begin-snippet: scryOptionsCommands
    /// <summary>
    /// How many commands may be in flight at once — accepted and not yet finished. Default zero, which
    /// maps no command route at all: a server serves writes because it said it would, and one that has
    /// not says nothing about the commands its model declares — every capability reads false.
    /// </summary>
    /// <remarks>
    /// One past the limit is answered <c>503</c> with a <c>Retry-After</c>, and nothing about it runs.
    /// When this is set, every command the model declares has to be handled — by a handler in the
    /// container or a dispatcher that claims it — or the server refuses to start.
    /// </remarks>
    public int MaxPendingCommands { get; set; }

    /// <summary>
    /// How many of those one caller may have in flight, where <see cref="Caller"/> can say who is
    /// asking. Default 20. One past it is answered <c>429</c>.
    /// </summary>
    public int MaxPendingCommandsPerCaller { get; set; } = 20;

    /// <summary>
    /// How long a command is waited for before it is answered as pending. Default one second. A command
    /// finishing inside it is answered with its outcome in one response; one that does not is answered
    /// with a stream: pending at once, then the outcome when it lands.
    /// </summary>
    public TimeSpan CommandSyncWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a finished command's outcome is kept for a client asking for it again by its id.
    /// Default five minutes. A command still pending after twelve times this is failed as having
    /// received no completion — a handler that never answers must not hold its place for ever.
    /// </summary>
    public TimeSpan CommandRetention { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The largest command body read, in bytes. Default 65,536 (64 KiB). One declaring more is refused
    /// with a <c>413</c> before it is read, and one sending more is refused once it passes the limit.
    /// </summary>
    public int MaxCommandBytes { get; set; } = 64 * 1024;
    // end-snippet

    internal List<Type> Dispatchers { get; } = [];

    /// <summary>
    /// Adds a dispatcher: something that carries commands elsewhere — a message bus, a queue — and
    /// reports their outcome back. Resolved from the container, and asked in the order added which
    /// commands it claims; a command no dispatcher claims is handled in-process, by the
    /// <see cref="ICommandHandler{TCommand}"/> the container supplies, and one two dispatchers claim is
    /// refused at startup.
    /// </summary>
    public void AddDispatcher<TDispatcher>()
        where TDispatcher : class, ICommandDispatcher
    {
        if (!Dispatchers.Contains(typeof(TDispatcher)))
        {
            Dispatchers.Add(typeof(TDispatcher));
        }
    }

    /// <summary>
    /// The same, for a dispatcher built by <paramref name="factory"/> — which <c>AddScry</c> registers
    /// as a singleton, so a bus adapter can bring its dispatcher along with its configuration.
    /// </summary>
    public void AddDispatcher<TDispatcher>(Func<IServiceProvider, TDispatcher> factory)
        where TDispatcher : class, ICommandDispatcher
    {
        AddDispatcher<TDispatcher>();
        DispatcherServices.Add(_ => _.TryAddSingleton(factory));
    }

    internal List<Action<IServiceCollection>> DispatcherServices { get; } = [];

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
    /// one caller's rows will hand them to the next. A server with a row or attachment policy, or a
    /// POCO source supplied by a factory, therefore has to set this before <see cref="QueryFreshness"/>
    /// is honoured; <c>MapScry</c> refuses to start otherwise, since the alternative is a leak that
    /// only shows up in production.
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
    /// Key used to seal keyset paging cursors (authenticated encryption, so a cursor is opaque as well
    /// as tamper-proof). When null a random per-process key is used, so cursors do not survive a
    /// restart or work across multiple instances — set a stable key for a scaled-out or
    /// restart-tolerant deployment. Sealing enforces the opaque-cursor contract and keeps the ordering
    /// keys it carries out of URLs and logs; it is not an authorization control (a decoded cursor is
    /// always re-validated and policy-filtered).
    /// </summary>
    public byte[]? CursorKey { get; set; }

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

    /// <summary>
    /// Whether <c>MapScry</c> may start with an opted-in entity or view type the context does not
    /// map. Off by default: such a source is advertised by introspection and answers nothing, so it
    /// is refused at startup naming the type. Set this where one model assembly serves several
    /// contexts and each opts in types the others map; a query naming a source this context lacks is
    /// then rejected as an unknown source, never faulted.
    /// </summary>
    public bool AllowUnmappedSources { get; set; }

    internal Type ContextType { get; private set; } = contextType;

    internal Dictionary<Type, Func<IServiceProvider, IQueryable>> PocoSources { get; } = [];

    // The POCO sources supplied by a factory, which is given the request's services and so may
    // answer by who asked.
    internal HashSet<Type> FactoryPocoSources { get; } = [];

    internal Dictionary<Type, (Type Policy, DeniedRowHandling Handling)> Policies { get; } = [];

    /// <summary>Registers the data for a <c>[QueryablePoco]</c> source, resolved per request.</summary>
    /// <remarks>
    /// The factory is given the request's services and may answer by who asked, so a source
    /// registered this way counts as one whose rows depend on the caller: with
    /// <see cref="QueryFreshness"/> set, <c>MapScry</c> refuses to start until <see cref="CacheScope"/>
    /// says what a cached response belongs to. Data that is the same for every caller is registered
    /// as the collection itself, which asks nothing of the caller.
    /// </remarks>
    public void AddPocoSource<T>(Func<IServiceProvider, IEnumerable<T>> factory)
        where T : class
    {
        PocoSources[typeof(T)] = services => factory(services).AsQueryable();
        FactoryPocoSources.Add(typeof(T));
    }

    /// <summary>Registers a fixed in-memory <c>[QueryablePoco]</c> source.</summary>
    public void AddPocoSource<T>(IEnumerable<T> items)
        where T : class
    {
        PocoSources[typeof(T)] = _ => items.AsQueryable();
        FactoryPocoSources.Remove(typeof(T));
    }

    /// <summary>
    /// Attaches a row/instance policy to an entity, replacing any <c>[ReturnableWith]</c> on that same
    /// type. Like the attribute it also covers every opted-in type deriving from that one, whose own
    /// policies narrow further rather than replace this one.
    /// </summary>
    public void AddPolicy<TEntity, TPolicy>()
        where TPolicy : IReturnablePolicy<TEntity> =>
        AddPolicy<TEntity, TPolicy>(DeniedRowHandling.Default);

    /// <summary>
    /// The same, saying what the policy's denied rows produce rather than taking the hide-everywhere
    /// default. The handling travels with this registration only: a base's policy keeps its own.
    /// </summary>
    public void AddPolicy<TEntity, TPolicy>(DeniedRowHandling handling)
        where TPolicy : IReturnablePolicy<TEntity> =>
        Policies[typeof(TEntity)] = (typeof(TPolicy), handling);

    internal Dictionary<Type, (Type Policy, LambdaExpression Version, DeniedRowHandling Handling)> CachedPolicies { get; } = [];

    /// <summary>
    /// Where cached row policies keep their answers. In this process by default, which is enough for a
    /// single server; a deployment running several, or one that would rather not decide every row again
    /// after a restart, sets a store of its own.
    /// </summary>
    public ICachedPolicyStore CachedPolicyStore { get; set; } = new MemoryCachedPolicyStore();

    /// <summary>
    /// The most rows one caller may be allowed by a cached policy before a query is refused rather than
    /// run. Null for no limit. Every allowed key travels to the database with each query, so this is
    /// what turns an allow-list that quietly grew unbounded into a message rather than a slow query.
    /// </summary>
    public int? MaxCachedPolicyKeys { get; set; }

    /// <summary>
    /// The most undecided rows one refresh of a cached policy may read and decide — on a scope
    /// nothing has been decided for yet, every row of the table. Null for no limit. A refresh under
    /// the bound counts before it reads, so a table past it costs one COUNT and a refusal naming this
    /// option rather than a materialization. The cold cost is paid per scope key — on a per-user
    /// scope, once by every new caller — and this is what keeps it from being the size of the table.
    /// </summary>
    public int? MaxCachedPolicyRows { get; set; }

    /// <summary>
    /// Attaches a row policy whose decision is too expensive to make in SQL. The policy answers one row
    /// at a time in C#; the server remembers the answers and composes a membership test over the keys
    /// this caller may see, wherever a row policy applies.
    /// </summary>
    /// <param name="version">
    /// A column that goes up whenever the row changes — a <c>ulong</c>-mapped <c>rowversion</c>, a
    /// counter, a last-modified stamp. It is what lets a new or changed row be decided on its first
    /// read without every other row being decided again, so index it.
    /// </param>
    /// <param name="handling">What a denied row produces; hidden everywhere by default.</param>
    /// <remarks>
    /// Registered in code rather than by attribute: the version column is an expression, and which
    /// store the answers live in is a property of the deployment rather than of the model. The rows
    /// are remembered by their primary key, derived the same way an attachment's is and checked
    /// against the real one at startup.
    /// </remarks>
    public void AddCachedPolicy<TEntity, TVersion, TPolicy>(
        Expression<Func<TEntity, TVersion>> version,
        DeniedRowHandling? handling = null)
        where TEntity : class
        where TVersion : struct
        where TPolicy : ICachedRowPolicy<TEntity> =>
        CachedPolicies[typeof(TEntity)] = (typeof(TPolicy), version, handling ?? DeniedRowHandling.Default);

    internal Func<IServiceProvider, IScryChangeBackplane>? Backplane { get; private set; }

    /// <summary>
    /// Carries this node's changes to the deployment's other nodes, and theirs to this one, so a write
    /// on any of them re-asks the live queries held by all. Unset — the default — a node hears only
    /// its own writes, which is everything a single server needs.
    /// </summary>
    /// <remarks>
    /// The backplane is built from the host's services, so it may take whatever it needs from them —
    /// a connection multiplexer, a message session. A deployment whose database can say when it was
    /// last written needs none: see <c>ChangeProbe</c>.
    /// </remarks>
    public void UseBackplane<TBackplane>()
        where TBackplane : class, IScryChangeBackplane =>
        Backplane = ActivatorUtilities.GetServiceOrCreateInstance<TBackplane>;

    /// <summary>The same, built by <paramref name="factory"/> rather than by its constructor.</summary>
    public void UseBackplane(Func<IServiceProvider, IScryChangeBackplane> factory) =>
        Backplane = factory;

    /// <summary>
    /// The same, for a backplane that needs services of its own beside it — something its transport
    /// resolves from the container and the backplane has to share, as a message handler and the
    /// backplane it hands its messages to do. <paramref name="services"/> is run by <c>AddScry</c>.
    /// </summary>
    public void UseBackplane(Func<IServiceProvider, IScryChangeBackplane> factory, Action<IServiceCollection> services)
    {
        Backplane = factory;
        BackplaneServices = services;
    }

    internal Action<IServiceCollection>? BackplaneServices { get; private set; }

    internal Dictionary<Type, Type> AttachmentPolicies { get; } = [];

    /// <summary>
    /// Attaches the authorization check for an entity's <c>[Attachment]</c> members, replacing any
    /// <c>[AttachmentWith]</c> on that same type. A type exposing an attachment must have one, here or
    /// as the attribute, or the server refuses to start.
    /// </summary>
    public void AddAttachmentPolicy<TEntity, TPolicy>()
        where TPolicy : IAttachmentPolicy<TEntity> =>
        AttachmentPolicies[typeof(TEntity)] = typeof(TPolicy);

    internal Dictionary<Type, Type> CommandPolicies { get; } = [];

    /// <summary>
    /// Attaches the policy deciding who may send a command — and, where it also implements
    /// <see cref="ICommandPolicy{TCommand, TEntity}"/>, against which rows — replacing any
    /// <c>[Command(Policy = ...)]</c> on the command. A command with no policy may be sent by anyone
    /// its endpoint admits.
    /// </summary>
    public void AddCommandPolicy<TCommand, TPolicy>()
        where TPolicy : ICommandPolicy<TCommand> =>
        CommandPolicies[typeof(TCommand)] = typeof(TPolicy);
}
