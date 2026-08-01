/// <summary>
/// The rules <see cref="Scry.ScryLinqAnalyzer"/> reports. Each one restates, at the call site, a
/// refusal that already exists further down the pipeline — in <c>QueryTranslator</c> when the query is
/// captured, or in the server's <c>QueryValidator</c> when the request arrives. The reasoning behind
/// each is in docs/linq-coverage.md, which every descriptor links to rather than paraphrasing.
/// </summary>
static class LinqDiagnostics
{
    const string category = "Scry";

    public static readonly DiagnosticDescriptor UnsupportedOperator = Rule(
        "SCRY100",
        "LINQ operator is not supported by Scry",
        "'{0}' is not part of the operator set Scry can carry, so this query fails when it is translated",
        "Scry's wire vocabulary is a closed set: every operator must be individually representable, validatable and rebindable on the server. An operator outside the set throws NotSupportedException when the query is captured.");

    public static readonly DiagnosticDescriptor Cast = Rule(
        "SCRY101",
        "Cast is not supported by Scry",
        "Cast is not supported by Scry — use OfType<{0}> to narrow by filtering",
        "Cast asserts that every row already is the target type, and EF carries that assertion in the materializer — while turning a row into an entity. A Scry query always ends in a projection, so no entity is constructed and the check never runs. Under table-per-hierarchy the query would neither filter nor fault: it would answer with exactly the rows the assertion existed to rule out, each with a null where the derived member should be.");

    public static readonly DiagnosticDescriptor ResultSelector = Rule(
        "SCRY102",
        "SelectMany with a result selector is not supported by Scry",
        "SelectMany with a result selector is not supported by Scry — flatten first, then Select",
        "A result selector produces a two-rooted row without a join's projection to name the sides, so there is no way to say which side each member reads.");

    public static readonly DiagnosticDescriptor Comparer = Rule(
        "SCRY103",
        "Comparer overloads are not supported by Scry",
        "'{0}' with a comparer is not supported by Scry — the comparison happens in the database, which cannot run a client-side comparer",
        "An IComparer or IEqualityComparer is client-side code. The operator it qualifies runs in SQL, so there is nothing on the wire that could carry it.");

    public static readonly DiagnosticDescriptor SingleUse = Rule(
        "SCRY104",
        "Operator may only appear once in a Scry query",
        "A Scry query may carry only one {0}; this is the second, and the server rejects the request",
        "Enforced server-side by QueryValidator, so writing two costs a round trip rather than a translation failure. Compose the work into the single occurrence instead.");

    public static readonly DiagnosticDescriptor OrderingKey = Rule(
        "SCRY105",
        "Ordering key must be a single value",
        "'{0}' takes a single value as its key — a constructed object has no ordering of its own",
        "The wire carries no constructed value outside a projection, and an anonymous type has no ordering to carry anyway. Order by one key and add the rest with ThenBy.");

    public static readonly DiagnosticDescriptor Projection = Rule(
        "SCRY106",
        "Projection must construct an object",
        "A Scry projection must construct an object — an anonymous type, a record, or an object initializer",
        "A response is a list of objects keyed by member name, which is what lets it materialize back into the type the client wrote. A bare value has no member name to key it by.");

    public static readonly DiagnosticDescriptor UnsupportedFunction = Rule(
        "SCRY107",
        "Function is not supported by Scry",
        "'{0}' is not one of the functions Scry can carry",
        "The callable set is closed, and deliberately smaller than what EF can translate: a function with no SQL Server translation is left out rather than shipped as a trap that fails at execution.");

    public static readonly DiagnosticDescriptor FormattedToString = Rule(
        "SCRY108",
        "ToString with a format is not supported by Scry",
        "ToString with a format is not supported by Scry — format the value after the query returns",
        "No provider translates it, and the SQL function that would express it reads the server's language, so the same row would format differently per connection. It appears to work in a projection only because EF evaluates it client-side once the rows are read.");

    public static readonly DiagnosticDescriptor SynchronousExecution = Rule(
        "SCRY109",
        "Scry query cannot be executed synchronously",
        "'{0}' executes the query where it stands, which a Scry source cannot do — {1}",
        "A Scry query is answered over HTTP. The capture-only provider throws on synchronous enumeration rather than blocking a request out of it.");

    public static readonly DiagnosticDescriptor UnorderedReverse = Rule(
        "SCRY110",
        "Reverse requires an ordered query",
        "Reverse requires a preceding OrderBy, as EF does",
        "There is no order to invert until one has been established. Enforced server-side by QueryValidator, so this costs a round trip rather than a translation failure.");

    public static readonly DiagnosticDescriptor ProjectedGroup = Rule(
        "SCRY111",
        "GroupJoin may not project its group",
        "A GroupJoin's group can only be folded to a scalar — '{0}' would put a nested collection in the response",
        "Projecting the group is what keeps collections aggregable and not projectable: a response never carries a nested collection. Aggregate the group instead.");

    public static readonly ImmutableArray<DiagnosticDescriptor> All =
    [
        UnsupportedOperator,
        Cast,
        ResultSelector,
        Comparer,
        SingleUse,
        OrderingKey,
        Projection,
        UnsupportedFunction,
        FormattedToString,
        SynchronousExecution,
        UnorderedReverse,
        ProjectedGroup
    ];

    // Warning rather than error throughout. The analyzer sees only what is written literally in the
    // chain, so a query composed through a helper it cannot follow is reported by the translator and
    // not here — and a rule that can be incomplete should not be able to break a build on its own.
    // Escalate the lot with 'dotnet_diagnostic.SCRY1XX.severity = error' in .editorconfig.
    static DiagnosticDescriptor Rule(string id, string title, string message, string description) =>
        new(
            id,
            title,
            message,
            category,
            DiagnosticSeverity.Warning,
            true,
            description,
            SupportedLinq.Docs);
}
