namespace Scry;

/// <summary>
/// How the disclosure audit is set up. Made by
/// <see cref="ScryOptions.UseDisclosureAudit(IScryDisclosureSink, Action{ScryDisclosureOptions}?)"/>,
/// which is also what turns the audit on.
/// </summary>
// begin-snippet: disclosureOptions
public sealed class ScryDisclosureOptions
{
    /// <summary>
    /// Who is asking, for a call whose transport did not say. The HTTP endpoints, the hub and MCP all
    /// say, with <see cref="ScryOptions.Caller"/>; this answers for a transport of a host's own
    /// calling the processor directly. Null, the default, reads the current request's caller through
    /// <c>IHttpContextAccessor</c> where there is one.
    /// </summary>
    /// <remarks>
    /// Read from the authenticated principal, never from something the caller supplied: this is the
    /// name every answer is recorded under.
    /// </remarks>
    public Func<IServiceProvider, string?>? Caller { get; set; }

    /// <summary>
    /// Whether an answer may be recorded with no caller. Off by default: an answer nobody can be
    /// named as having received is refused rather than sent, since "who received this" is the
    /// question the record exists to answer.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// A key that makes every address an HMAC-SHA-256 rather than a plain SHA-256. Null, the
    /// default, leaves them plain.
    /// </summary>
    /// <remarks>
    /// An address stays in the record after the content it names has been erased, and a plain hash
    /// of a value with few possibilities — a status, a yes or a no — can be confirmed by guessing.
    /// Under a key it cannot, by anyone who lacks the key. Changing the key starts addresses afresh:
    /// content recorded under the old one is not recognised as the same.
    /// </remarks>
    public byte[]? AddressKey { get; set; }

    /// <summary>
    /// Whether binary values — an attachment's bytes, a <c>byte[]</c> member — are kept, rather than
    /// recorded by digest and length alone. Off by default.
    /// </summary>
    public bool StoreBinaryContent { get; set; }

    /// <summary>
    /// How much of a stream is held back while the record of it is accepted, in bytes. Default 16,384.
    /// A stream is released a chunk at a time, each only once the sink holds it; zero makes every row
    /// a chunk of its own.
    /// </summary>
    public int StreamChunkBytes { get; set; } = 16 * 1024;

    /// <summary>What this node is recorded as. The machine's name by default.</summary>
    public string Node { get; set; } = Environment.MachineName;

    /// <summary>The clock events are timed by. The system's by default.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    // end-snippet

    /// <summary>
    /// Puts a journal file on this machine in front of the sink: an answer is accepted once its
    /// record is flushed to <paramref name="directory"/>, and the sink is handed it afterwards. So a
    /// store that is slow, or down for a while, costs a caller nothing.
    /// </summary>
    /// <param name="directory">
    /// Where the journal keeps its files. One process's alone, on a disk that outlives the process:
    /// in a container, a persistent volume.
    /// </param>
    /// <param name="configure">How large the journal may grow, and how it paces itself.</param>
    /// <remarks>
    /// What the journal buys is the request path no longer waiting on the store. What it costs is
    /// that "accepted" then means "on this machine's disk": see <see cref="ScryDisclosureJournal"/>
    /// for what that survives and what it does not.
    /// </remarks>
    public void UseJournal(string directory, Action<ScryDisclosureJournalOptions>? configure = null)
    {
        var journal = new ScryDisclosureJournalOptions();
        configure?.Invoke(journal);
        JournalDirectory = directory;
        Journal = journal;
    }

    /// <summary>
    /// Says what identifies a row of a source that has no key of its own — a view, a source supplied
    /// from memory, a table mapped with none — so that a row sent from it is recorded as that row.
    /// </summary>
    /// <param name="members">
    /// The members that together identify one row, in the order a row is to be asked about by: each a
    /// property of the source, as <c>_ =&gt; _.Code</c>.
    /// </param>
    /// <remarks>
    /// Not needed for an entity with a primary key, which is found in the model. Where it is given for
    /// one, it replaces that key in the record.
    /// </remarks>
    public void Key<TSource>(params Expression<Func<TSource, object?>>[] members)
    {
        if (members.Length == 0)
        {
            throw new ArgumentException($"A key for '{typeof(TSource).Name}' names at least one member. A source that has nothing to identify a row by is acknowledged with {nameof(Unkeyed)} instead.");
        }

        Keys[typeof(TSource)] = [.. members.Select(Member)];
        Acknowledged.Remove(typeof(TSource));
    }

    /// <summary>
    /// Acknowledges that a source has nothing to identify a row by, so what is sent from it is
    /// recorded as content and as members with no row to hang them on. Who received a given row of it
    /// then cannot be asked; who received any of it, and what, still can.
    /// </summary>
    /// <remarks>
    /// A server whose model has such a source refuses to start until each is either given a key or
    /// acknowledged here: a record with no row identity is a decision, and is not made by default.
    /// </remarks>
    public void Unkeyed<TSource>()
    {
        Acknowledged.Add(typeof(TSource));
        Keys.Remove(typeof(TSource));
    }

    /// <summary>
    /// Leaves a source out of the record: an answer that reads nothing but excluded sources is not
    /// recorded, is not marked <c>no-store</c> for the audit's sake, and needs no caller. For a
    /// source whose rows are nobody's to ask after — a list of countries, a calendar of holidays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is about the answer, and errs towards recording. An answer that reads an excluded
    /// source and one that is not — through a navigation, a join, a subquery, a set operation, in a
    /// filter as much as in a projection — is recorded whole, the excluded source's part included:
    /// leaving that part out would leave a record that could not be put back together into what was
    /// sent. So excluding a source never hides what was sent of another.
    /// </para>
    /// <para>
    /// Said of a type, it holds for the types derived from it. An excluded source needs neither a
    /// key nor <see cref="Unkeyed{TSource}"/> for the server to start; where an answer that is
    /// recorded reads one with no key, its rows are recorded as content with no row to hang them on.
    /// </para>
    /// </remarks>
    public void Exclude<TSource>() =>
        Excluded.Add(typeof(TSource));

    internal HashSet<Type> Excluded { get; } = [];

    // Said of a type or of one it derives from, as a key and an acknowledgement are.
    internal bool Excludes(Type type)
    {
        if (Excluded.Count == 0)
        {
            return false;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        for (var declared = type; declared is not null; declared = declared.BaseType)
        {
            if (Excluded.Contains(declared))
            {
                return true;
            }
        }

        return false;
    }

    static PropertyInfo Member<TSource>(Expression<Func<TSource, object?>> member)
    {
        var body = member.Body;

        // A value-typed member arrives boxed to fit the delegate.
        if (body is UnaryExpression {NodeType: ExpressionType.Convert} boxed)
        {
            body = boxed.Operand;
        }

        if (body is MemberExpression {Member: PropertyInfo property, Expression: ParameterExpression})
        {
            return property;
        }

        throw new ArgumentException($"A key member is a property read straight off the row, as _ => _.Code; '{member}' is not one.");
    }

    internal Dictionary<Type, IReadOnlyList<PropertyInfo>> Keys { get; } = [];

    internal HashSet<Type> Acknowledged { get; } = [];

    internal string? JournalDirectory { get; private set; }

    internal ScryDisclosureJournalOptions? Journal { get; private set; }

    internal IScryDisclosureSink? Sink { get; set; }

    internal Func<IServiceProvider, IScryDisclosureSink>? SinkFactory { get; set; }

    internal Action<IServiceCollection>? Services { get; set; }

    // The sink this audit records through, made from what the host passed, with the journal in front
    // of it where one was asked for. Thrown rather than skipped where there is none: an audit that
    // was asked for and cannot record must never read as an audit that was not asked for.
    internal IScryDisclosureSink Build(IServiceProvider services)
    {
        var sink = Sink ??
                   SinkFactory?.Invoke(services) ??
                   throw new ScryDisclosureException("The disclosure audit is on but has no sink to record with. Pass one to ScryOptions.UseDisclosureAudit.");
        if (JournalDirectory is { } directory)
        {
            return new ScryDisclosureJournal(directory, sink, Journal)
            {
                // A sink made by the factory was made for this, and closes with it.
                OwnsInner = Sink is null
            };
        }

        return sink;
    }
}
