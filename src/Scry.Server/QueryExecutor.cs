/// <summary>
/// Orchestrates the full server pipeline for one request: validate against the allow-list, resolve
/// the source, apply the row policy, rebind the AST to an EF query, execute, and shape the result.
/// </summary>
sealed class QueryExecutor(Schema schema, ScryOptions options)
{
    QueryValidator validator = new(schema, options);

    /// <summary>
    /// A list result that has not been read yet: the row source, how to shape each row, and whether
    /// the rows arrive as <see cref="DistinctRow"/> records rather than shaped arrays. Materializing it
    /// and streaming it differ only in how the rows are pulled, so both go through one pipeline walk.
    /// </summary>
    internal readonly record struct RowSet(
        IQueryable Rows,
        ProjectionPlan Plan,
        bool Deduplicated,
        BinaryPartCollector? Binary = null);

    /// <summary>
    /// A page's rows, already read, plus what the envelope around them says. Read rather than unread
    /// because a page is bounded and its extra row has to be fetched to know whether a further page
    /// exists — so unlike a <see cref="RowSet"/> there is nothing left to defer.
    /// </summary>
    internal readonly record struct PageSet(
        IReadOnlyList<object[]> Rows,
        ProjectionPlan Plan,
        bool HasMore,
        string? Cursor,
        BinaryPartCollector? Binary = null);

    /// <summary>
    /// What a folding terminal produced, before anything has been written: a scalar's value, or the one
    /// row and the plan that shapes it. Held unserialized like a <see cref="RowSet"/> is unread, so the
    /// buffered path writes it straight into the response and the general path builds the
    /// <see cref="JsonElement"/> it returns from the same thing.
    /// </summary>
    internal readonly record struct Terminal(
        ResultKind Kind,
        object? Value,
        object[]? Row,
        ProjectionPlan? Plan,
        BinaryPartCollector? Binary);

    public QueryResponse Execute(QueryRequest request, DbContext db, CallScope scope)
    {
        var plan = Walk(request, db, scope);
        Prepare(plan, scope);
        if (plan.Fold is { } fold)
        {
            return Materialize(fold.Finish(Execution.Run(fold.Query, fold.Call)));
        }

        if (plan.Page is { } page)
        {
            return Envelope(page.Finish(page.Rows.ToList()));
        }

        var set = plan.Rows!.Value;
        var shaped = new List<Dictionary<string, object?>>();
        foreach (var row in set.Rows)
        {
            shaped.Add(ShapeRow(row!, set));
        }

        return ListResponse(shaped);
    }

    /// <summary>
    /// <see cref="Execute"/> with every question the database is asked awaited rather than blocked
    /// on: the cached policies brought up to date, the probes, and the query itself.
    /// </summary>
    public async ValueTask<QueryResponse> ExecuteAsync(QueryRequest request, DbContext db, CallScope scope, Cancel cancel)
    {
        var plan = Walk(request, db, scope);
        await PrepareAsync(plan, scope, cancel);
        if (plan.Fold is { } fold)
        {
            return Materialize(fold.Finish(await Execution.RunAsync(fold.Query, fold.Call, cancel)));
        }

        if (plan.Page is { } page)
        {
            return Envelope(page.Finish(await Execution.ReadAsync(page.Rows, cancel)));
        }

        var set = plan.Rows!.Value;
        var shaped = new List<Dictionary<string, object?>>();
        await foreach (var row in Enumerate(set, cancel))
        {
            shaped.Add(ShapeRow(row, set));
        }

        return ListResponse(shaped);
    }

    static QueryResponse Envelope(PageSet paged)
    {
        var envelope = new ScryPage<Dictionary<string, object?>>(
            [.. paged.Rows.Select(_ => Shape(_, paged.Plan, paged.Binary))],
            paged.HasMore,
            paged.Cursor);
        return QueryResponse.Create(ResultKind.Page, JsonSerializer.SerializeToElement(envelope, ScryJson.Options));
    }

    static QueryResponse ListResponse(List<Dictionary<string, object?>> shaped) =>
        QueryResponse.Create(ResultKind.List, JsonSerializer.SerializeToElement(shaped, ScryJson.Options));

    /// <summary>
    /// Everything the database is asked before the query itself: the cached policies brought up to
    /// date, then each probe in the order the walk met it. Both are questions a plan holds unasked,
    /// so the blocking surface and the endpoint each ask them their own way.
    /// </summary>
    static void Prepare(Plan plan, CallScope scope)
    {
        scope.Cached.Refresh();
        foreach (var probe in plan.Probes)
        {
            probe.Ensure();
        }
    }

    static async ValueTask PrepareAsync(Plan plan, CallScope scope, Cancel cancel)
    {
        await scope.Cached.RefreshAsync(cancel);
        foreach (var probe in plan.Probes)
        {
            await probe.EnsureAsync(cancel);
        }
    }

    /// <summary>
    /// Executes like <see cref="Execute"/>, but writes the complete response envelope straight into
    /// <paramref name="output"/> — every result kind, never passing through dictionaries or a
    /// <see cref="JsonElement"/>. The returned row count is what the result counts: the rows written
    /// for a list or page, one or none for a single row, and nothing for a scalar, which folds the
    /// rows away.
    /// </summary>
    /// <remarks>
    /// This is also the only place that knows the projection plan before the first row is read, so it
    /// is where <paramref name="spill"/> is told whether it may send anything early. The decision is
    /// the plan's rather than the data's — the same rule <c>StreamBuffered</c> applies to a different
    /// question — because a plan carrying no binary slot can never produce a part, and so can never
    /// need one to precede JSON that has already gone out.
    /// </remarks>
    public async ValueTask<(ResultKind Kind, int? Rows)> ExecuteBufferedAsync(
        QueryRequest request,
        DbContext db,
        CallScope scope,
        string stamp,
        IBufferWriter<byte> output,
        ResponseSpill? spill,
        Cancel cancel)
    {
        var plan = Walk(request, db, scope);
        await PrepareAsync(plan, scope, cancel);

        // A terminal folded its rows away, so there is nothing to spill and permission stays withheld.
        if (plan.Fold is { } fold)
        {
            var folded = fold.Finish(await Execution.RunAsync(fold.Query, fold.Call, cancel));
            return (folded.Kind, ResponseWriter.WriteTerminal(output, folded, stamp));
        }

        if (plan.Page is { } page)
        {
            var paged = page.Finish(await Execution.ReadAsync(page.Rows, cancel));
            spill?.AllowSpill(paged.Plan.BinarySlots is null);
            return (ResultKind.Page, await ResponseWriter.WritePageAsync(output, spill, paged, stamp, cancel));
        }

        var rowSet = plan.Rows!.Value;
        spill?.AllowSpill(rowSet.Plan.BinarySlots is null);
        return (ResultKind.List, await ResponseWriter.WriteListAsync(output, spill, rowSet, stamp, cancel));
    }

    /// <summary>
    /// Prepares a list-shaped query for streaming. Everything a request can be rejected for has already
    /// happened by the time this returns — validation runs to completion before anything is rebound —
    /// so a caller that has a <see cref="RowSet"/> in hand can commit to a success status before
    /// writing a row.
    /// </summary>
    public RowSet Stream(QueryRequest request, DbContext db, CallScope scope)
    {
        var plan = Walk(request, db, scope);
        var set = Streamable(plan);
        Prepare(plan, scope);
        return set;
    }

    /// <summary>The same, awaiting what the database is asked before the first row.</summary>
    public async ValueTask<RowSet> StreamAsync(QueryRequest request, DbContext db, CallScope scope, Cancel cancel)
    {
        var plan = Walk(request, db, scope);
        var set = Streamable(plan);
        await PrepareAsync(plan, scope, cancel);
        return set;
    }

    static RowSet Streamable(Plan plan)
    {
        if (plan.Rows is { } set)
        {
            return set;
        }

        // A page is bounded and answered whole, so it is as unstreamable as a folding terminal and
        // says so the same way.
        var kind = plan.Page is null ? plan.Fold!.Value.Kind : ResultKind.Page;
        throw new ScryValidationException(
            $"Only a query that returns rows can be streamed; this one returns {kind}. Drop the terminal operator, or use the non-streaming endpoint.");
    }

    /// <summary>
    /// Pulls a <see cref="RowSet"/>'s rows without materializing them. A provider that enumerates
    /// asynchronously — every EF one does — is read that way, so a streamed response never buffers the
    /// result server-side and never blocks a request thread on the database. An in-memory source (a
    /// POCO one) has nothing to await and is read directly.
    /// </summary>
    public static IAsyncEnumerable<object> Enumerate(RowSet set, Cancel cancel) =>
        (IAsyncEnumerable<object>)enumerators
            .GetOrAdd(
                set.Rows.ElementType,
                _ => typeof(QueryExecutor)
                    .GetMethod(nameof(EnumerateTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(_))
            .Invoke(null, [set.Rows, cancel])!;

    static readonly ConcurrentDictionary<Type, MethodInfo> enumerators = new();

    static async IAsyncEnumerable<object> EnumerateTyped<T>(IQueryable<T> rows, [EnumeratorCancellation] Cancel cancel)
    {
        if (rows is IAsyncEnumerable<T> asynchronous)
        {
            await foreach (var row in asynchronous.WithCancellation(cancel))
            {
                yield return row!;
            }

            yield break;
        }

        foreach (var row in rows)
        {
            cancel.ThrowIfCancellationRequested();
            yield return row!;
        }
    }

    /// <summary>Shapes one row of a <see cref="RowSet"/> into its response object.</summary>
    internal static Dictionary<string, object?> ShapeRow(object row, RowSet set) =>
        set.Deduplicated
            ? Shape(ExpressionBuilder.ReadDistinctRow(row), set.Plan, set.Binary)
            : Shape((object[])row, set.Plan, set.Binary);

    /// <summary>
    /// Builds a request into its EF query without executing it — for reading back the SQL it would
    /// run. A terminal folds the rows to a value the database has to be asked for, so a request
    /// carrying one is refused rather than run: a preview that executed would not be one.
    /// </summary>
    public RowSet Build(QueryRequest request, DbContext db, CallScope scope) =>
        Walk(request, db, scope, buildOnly: true).Rows ??
        throw new ScryValidationException("The query produced no rows to read SQL from.");

    /// <summary>
    /// What one request resolved to, short of running it: the probes to ask first, in the order the
    /// walk met them, and then exactly one of a call the provider folds to a value or a row, a page to
    /// read, or the unread rows of a list. Held unrun so the two ways of running a request — blocking,
    /// for a processor hosted outside HTTP, and awaiting, for the endpoint — differ only in how the
    /// database is asked, never in what.
    /// </summary>
    internal readonly record struct Plan(
        IReadOnlyList<DeniedRowProbe> Probes,
        Fold? Fold,
        PagePlan? Page,
        RowSet? Rows);

    /// <summary>
    /// A folding terminal, unrun: the call that answers it, over the query it folds. A scalar's call
    /// answers with the value; a single row's answers with the shaped <c>object[]</c> that
    /// <paramref name="Plan"/> describes.
    /// </summary>
    internal readonly record struct Fold(IQueryable Query, MethodCallExpression Call, ProjectionPlan? Plan, BinaryPartCollector? Binary)
    {
        public ResultKind Kind =>
            Plan is null ? ResultKind.Scalar : ResultKind.Single;

        public Terminal Finish(object? value) =>
            Plan is { } plan ? Single((object[]?)value, plan, Binary) : Scalar(value);
    }

    /// <summary>
    /// A page, unread: the rows to fetch — one more than the page holds, to learn whether a further
    /// page exists — and how what came back becomes the page and its envelope.
    /// </summary>
    internal readonly record struct PagePlan(IQueryable<object[]> Rows, Func<List<object[]>, PageSet> Finish);

    // Walks the pipeline once and plans exactly one of three things: a folding terminal's call, the
    // unread rows of a list result, which the caller materializes or streams, or a page's fetch.
    Plan Walk(
        QueryRequest request,
        DbContext db,
        CallScope scope,
        bool buildOnly = false)
    {
        var source = validator.Validate(request);
        var elementType = source.ClrType;

        // A query that runs reads rows, so a cached policy's answers are brought up to date for it. A
        // SQL preview reads none and leaves them as they are.
        scope = scope with {EnsureCachedFreshness = !buildOnly};

        // Built per request so a node that reads another source resolves it the same way the root was
        // resolved — through the schema, and policy-filtered — rather than reaching a DbSet directly.
        // The same resolution backs a traversal into a policied source, which is read through its
        // policy rather than off the owner it was reached from.
        // The questions asked of the database before the query runs, in the order the walk meets
        // them. A SQL preview runs nothing, so it asks nothing either.
        var probes = new List<DeniedRowProbe>();
        var resolve = (string name) =>
        {
            // A membership set reads the other source as a list, so its list position answers for
            // it — asked about the source whole, since the filter a membership test carries is
            // applied where the set is built, after this. Erring wide is the documented direction.
            if (!buildOnly &&
                schema.TryGetSource(name, out var setSource) &&
                ProbeSideSource(setSource, db, scope) is { } setProbe)
            {
                probes.Add(setProbe);
            }

            return ResolveSource(name, db, scope);
        };
        var builder = new ExpressionBuilder(
            schema,
            options,
            resolve,
            // A traversal into a source whose policy reports denials plans a question about it as it
            // is built, asked with the others before anything this query runs.
            new(
                schema,
                db.Model,
                (name, include) => ResolveSource(name, db, scope, include),
                buildOnly ? null : probes));

        var query = source.Resolve(db, scope.Services);
        query = ApplyPolicy(query, source, db, scope);
        // How much of the policy chain the query already carries, so narrowing to a subclass applies
        // only what that subclass adds rather than repeating its base's.
        var appliedPolicies = source.Policies.Count;

        GroupByOp? groupBy = null;
        SelectOp? select = null;
        QueryOp? terminal = null;
        Node? groupFilter = null;
        JoinOp? join = null;
        SetOp? set = null;
        // Captured alongside inline application so the page terminal can rebuild the ordering into a
        // total order (append the primary key) and a keyset seek predicate.
        List<(Node Key, bool Descending)> orderings = [];
        // A cursor can only append its primary-key tiebreak when the ordering is the trailing restricting
        // op (so the query is still IOrderedQueryable) and no offset (Skip/Take) is in play.
        var tailIsOrdered = false;
        var sawSkipOrTake = false;
        // Distinct deduplicates the projected rows, so it is applied after the projection is built
        // rather than inline with the operators over the entity query. Ordering and paging written
        // after it describe those deduplicated values and are held back with it.
        var distinct = false;
        List<QueryOp> afterDistinct = [];
        // What the denied-row probe replays over a root carrying only the policies that hide, to ask
        // whether one that fails the request instead denied a row this query would otherwise have read.
        var probeSteps = new ProbeSteps();

        foreach (var op in request.Pipeline)
        {
            switch (op)
            {
                // A Where after GroupBy filters the groups, not the rows, so it cannot be applied to the
                // entity query — it waits for the grouping to be built. Several of them conjoin.
                case WhereOp having when groupBy is not null:
                    if (groupFilter is null)
                    {
                        groupFilter = having.Predicate;
                    }
                    else
                    {
                        groupFilter = new BinaryNode(BinaryOp.AndAlso, groupFilter, having.Predicate);
                    }
                    break;
                case WhereOp where:
                    tailIsOrdered = false;
                    var filter = builder.BuildPredicate(where.Predicate, elementType);
                    probeSteps.Where(filter);
                    query = Apply(query, "Where", filter);
                    break;
                case ReverseOp:
                    // The declared ordering no longer describes the rows, so a keyset cursor cannot
                    // seek over it; paging falls back to offset.
                    tailIsOrdered = false;
                    query = query.Provider.CreateQuery(
                        CallQueryable("Reverse", [query.ElementType], query.Expression));
                    break;
                // Over a deduplicated query the ordering describes the projected values, so it is held
                // back and applied to those rather than to the rows that fed them.
                case OrderByOp deduplicated when distinct:
                    afterDistinct.Add(deduplicated);
                    break;
                case SkipOp or TakeOp when distinct:
                    probeSteps.Stop();
                    afterDistinct.Add(op);
                    break;
                case OrderByOp orderBy:
                    orderings.Add((orderBy.Key, orderBy.Descending));
                    tailIsOrdered = true;
                    query = ApplyOrder(query, builder.BuildKeySelector(orderBy.Key, elementType), orderBy.Descending, then: false);
                    break;
                case ThenByOp thenBy:
                    orderings.Add((thenBy.Key, thenBy.Descending));
                    tailIsOrdered = true;
                    query = ApplyOrder(query, builder.BuildKeySelector(thenBy.Key, elementType), thenBy.Descending, then: true);
                    break;
                case SkipOp skip:
                    sawSkipOrTake = true;
                    tailIsOrdered = false;
                    // Paging selects among the rows that matched, so nothing after it says which rows
                    // the query asked for: the probe keeps the filters written before it and asks about
                    // the unpaged set, which is the set a policy denying a row would have hidden from.
                    probeSteps.Stop();
                    query = ApplyPaging(query, "Skip", skip.Count);
                    break;
                case TakeOp take:
                    sawSkipOrTake = true;
                    tailIsOrdered = false;
                    probeSteps.Stop();
                    query = ApplyPaging(query, "Take", take.Count);
                    break;
                case OfTypeOp narrowed:
                    tailIsOrdered = false;
                    if (!schema.TryGetSource(narrowed.Type, out var derived))
                    {
                        throw new ScryValidationException($"Unknown source '{narrowed.Type}'.");
                    }

                    query = query.Provider.CreateQuery(
                        CallQueryable("OfType", [derived.ClrType], query.Expression));

                    // The derived type's own policies apply on top of the base's. Both narrow, so the
                    // rows that survive are those a direct query of either source would have returned.
                    probeSteps.Narrow(derived, appliedPolicies);
                    query = ApplyPolicies(query, derived, appliedPolicies, db, scope);
                    appliedPolicies = derived.Policies.Count;
                    elementType = derived.ClrType;
                    break;

                case SelectManyOp flatten:
                    tailIsOrdered = false;
                    // The rows are the collection's from here on, not the root's.
                    probeSteps.Stop();
                    var (collection, child) = builder.BuildCollectionSelector(flatten.Path, elementType);
                    query = query.Provider.CreateQuery(
                        CallQueryable(
                            "SelectMany",
                            [elementType, child],
                            query.Expression,
                            Expression.Quote(collection)));
                    elementType = child;

                    // The policies the rows now carry are the element's, not the root's: the flatten
                    // read a policied element through its whole chain (the correlated rewrite), and an
                    // unpolicied one through none. A narrowing after the flatten skips exactly that
                    // much of the derived chain — counting the root's here would skip the derived
                    // type's own policies instead.
                    appliedPolicies = schema.TryGetPoliciedSource(child, out var elementSource)
                        ? elementSource.Policies.Count
                        : 0;
                    break;
                case DistinctOp:
                    distinct = true;
                    break;
                case JoinOp joined:
                    join = joined;
                    break;
                case SetOp combined:
                    set = combined;
                    break;
                case GroupByOp group:
                    groupBy = group;
                    break;
                case SelectOp projection:
                    select = projection;
                    break;
                default:
                    terminal = op;
                    break;
            }
        }

        // Checked here, against the terminal the loop above already identified, rather than by
        // re-deciding what a terminal is: this is the last point before one would be executed, and
        // reading it off the executor's own classification is what keeps the two from drifting.
        if (buildOnly &&
            terminal is not null)
        {
            var name = terminal.GetType().Name.Replace("Op", "");
            throw new ScryValidationException(
                $"SQL can only be shown for a query that returns rows; this one ends in {name}, which the database answers rather than lists. Drop the terminal to see the SQL underneath it.");
        }

        // Every terminal predicate narrows the rows before anything else the terminal does, so they are
        // all applied here rather than each terminal repeating it.
        if (TerminalPredicate(terminal) is { } predicate)
        {
            var narrowing = builder.BuildPredicate(predicate, elementType);
            probeSteps.Where(narrowing);
            query = Apply(query, "Where", narrowing);
        }

        // Before anything executes: a denied row must not reach a result, and a folding terminal would
        // leave nothing to inspect after one had. Skipped for a SQL preview, which runs nothing.
        if (!buildOnly &&
            ProbeDeniedRows(source, probeSteps, terminal, db, scope) is { } rootProbe)
        {
            probes.Add(rootProbe);
        }

        if (terminal is PageOp paging)
        {
            return new(probes, null, Page(builder, query, elementType, select, orderings, tailIsOrdered && !sawSkipOrTake, paging, source, db, scope.Binary), null);
        }

        // A join projects straight to the shaped row, so it replaces the projection step entirely and
        // the folding terminals below fold the joined rows.
        if (join is not null)
        {
            // The inner side reads the joined source as a list, narrowed by the filters it carries.
            if (!buildOnly &&
                schema.TryGetSource(join.Root, out var joinedSource) &&
                ProbeSideSource(joinedSource, db, scope, side => SideFilters(builder, side, joinedSource.ClrType, join.InnerOps, join.InnerPredicate)) is { } innerProbe)
            {
                probes.Add(innerProbe);
            }

            var (joined, joinPlan) = BuildJoined(builder, query, elementType, join, db, scope);

            return terminal switch
            {
                CountOp => Folds(probes, joined, "Count"),
                LongCountOp => Folds(probes, joined, "LongCount"),
                AnyOp => Folds(probes, joined, "Any"),
                FirstOp firstRow => Picks(probes, joined, firstRow.OrDefault ? "FirstOrDefault" : "First", joinPlan, scope.Binary),
                SingleOp singleRow => Picks(probes, joined, singleRow.OrDefault ? "SingleOrDefault" : "Single", joinPlan, scope.Binary),
                _ => Lists(probes, new(joined, joinPlan, Deduplicated: false, scope.Binary))
            };
        }

        // Both sides of a set operation are materialized as the same row type, which is what lets a
        // provider compare them — and, for every kind but Concat, deduplicate across them.
        if (set is not null)
        {
            if (builder.BuildDistinctRow(select!.Projection, elementType) is not var (leftSelector, shape, binarySlots))
            {
                throw new ScryValidationException(
                    $"A set operation is limited to {DistinctRow.ByArity.Length} projected members.");
            }

            // The operand reads its source as a list, narrowed by the filters it carries.
            if (!buildOnly &&
                schema.TryGetSource(set.Root, out var operandSource) &&
                ProbeSideSource(operandSource, db, scope, side => SideFilters(builder, side, operandSource.ClrType, set.OperandOps, set.Predicate)) is { } operandProbe)
            {
                probes.Add(operandProbe);
            }

            var otherSource = ResolveSource(set.Root, db, scope);
            var otherType = otherSource.ElementType;
            if (set.OperandOps is { } operandOps)
            {
                otherSource = ApplySideOps(builder, otherSource, operandOps, otherType);
            }
            else if (set.Predicate is { } otherPredicate)
            {
                otherSource = Apply(otherSource, "Where", builder.BuildPredicate(otherPredicate, otherType));
            }

            if (builder.BuildDistinctRow(set.Projection, otherType) is not var (rightSelector, _, _) ||
                rightSelector.ReturnType != leftSelector.ReturnType)
            {
                throw new ScryValidationException(
                    "Both sides of a set operation must project members of the same types.");
            }

            var rowType = leftSelector.ReturnType;
            var left = ApplySelectTyped(query, leftSelector);
            var combined = left.Provider.CreateQuery(
                CallQueryable(
                    set.Kind.ToString(),
                    [rowType],
                    left.Expression,
                    ApplySelectTyped(otherSource, rightSelector).Expression));

            switch (terminal)
            {
                case CountOp:
                    return Folds(probes, combined, "Count");
                case LongCountOp:
                    return Folds(probes, combined, "LongCount");
                case AnyOp:
                    return Folds(probes, combined, "Any");
            }

            return Lists(probes, new(combined, new(leftSelector, shape, binarySlots), Deduplicated: true, scope.Binary));
        }

        // Ordering, paging and folding all need the deduplicated rows to have equality and ordering of
        // their own, which a shaped object[] does not. Those go through a row type instead; plain
        // enumeration keeps the object[] path below, which has no arity limit.
        if (distinct &&
            (terminal is CountOp or LongCountOp or AnyOp || afterDistinct.Count > 0))
        {
            if (builder.BuildDistinctRow(select!.Projection, elementType) is not var (selector, shape, binarySlots))
            {
                throw new ScryValidationException(
                    $"A Distinct query of more than {DistinctRow.ByArity.Length} projected members can only be enumerated.");
            }

            var rowType = selector.ReturnType;
            var selected = ApplySelectTyped(query, selector);
            var deduped = selected.Provider.CreateQuery(
                CallQueryable("Distinct", [rowType], selected.Expression));

            foreach (var op in afterDistinct)
            {
                deduped = op switch
                {
                    OrderByOp order => ApplyOrder(
                        deduped,
                        ExpressionBuilder.BuildDistinctRowKey(rowType, DistinctLeaf(select.Projection, order.Key)),
                        order.Descending,
                        then: false),
                    SkipOp skip => ApplyPaging(deduped, "Skip", skip.Count),
                    TakeOp take => ApplyPaging(deduped, "Take", take.Count),
                    _ => throw new ScryValidationException($"Unsupported operator '{op.GetType().Name}' after Distinct.")
                };
            }

            switch (terminal)
            {
                case CountOp:
                    return Folds(probes, deduped, "Count");
                case LongCountOp:
                    return Folds(probes, deduped, "LongCount");
                case AnyOp:
                    return Folds(probes, deduped, "Any");
            }

            return Lists(probes, new(deduped, new(selector, shape, binarySlots), Deduplicated: true, scope.Binary));
        }

        // Every other folding terminal reads the rows themselves and projects nothing. None of them can
        // co-occur with a Distinct: the validator allows only the three folded above.
        switch (terminal)
        {
            case CountOp:
                return Folds(probes, query, "Count");
            case LongCountOp:
                return Folds(probes, query, "LongCount");
            case AnyOp:
                return Folds(probes, query, "Any");
            case AllOp all:
                return Folds(probes, query, "All", Expression.Quote(builder.BuildPredicate(all.Predicate, elementType)));
            case AggregateOp aggregate:
                return new(probes, Aggregate(builder, query, aggregate, elementType), null, null);
        }

        var (projected, plan) = BuildProjected(builder, query, elementType, groupBy, select, groupFilter);

        if (distinct)
        {
            projected = ApplyDistinct(projected, source.Kind);
        }

        return terminal switch
        {
            FirstOp first => Picks(probes, projected, first.OrDefault ? "FirstOrDefault" : "First", plan, scope.Binary),
            SingleOp single => Picks(probes, projected, single.OrDefault ? "SingleOrDefault" : "Single", plan, scope.Binary),
            LastOp last => Picks(probes, projected, last.OrDefault ? "LastOrDefault" : "Last", plan, scope.Binary),
            _ => Lists(probes, new(projected, plan, Deduplicated: false, scope.Binary))
        };
    }

    static Plan Folds(List<DeniedRowProbe> probes, IQueryable query, string method, params Expression[] arguments) =>
        new(probes, new Fold(query, CallQueryable(method, [query.ElementType], [query.Expression, .. arguments]), null, null), null, null);

    // The one-argument overload: the terminal's predicate, where it had one, was applied as a filter
    // before this.
    static Plan Picks(List<DeniedRowProbe> probes, IQueryable<object[]> rows, string method, ProjectionPlan plan, BinaryPartCollector? binary) =>
        new(probes, new Fold(rows, CallQueryable(method, [typeof(object[])], rows.Expression), plan, binary), null, null);

    static Plan Lists(List<DeniedRowProbe> probes, RowSet rows) =>
        new(probes, null, null, rows);

    /// <summary>
    /// Resolves the joined source and combines it with the outer query. The inner source goes through
    /// the same resolution and <b>row policy</b> the outer did, before the two meet — so a join can
    /// only narrow, and no row hidden from a direct query of the inner source is observable through one.
    /// </summary>
    (IQueryable<object[]> Query, ProjectionPlan Plan) BuildJoined(
        ExpressionBuilder builder,
        IQueryable outer,
        Type outerType,
        JoinOp join,
        DbContext db,
        CallScope scope)
    {
        if (!schema.TryGetSource(join.Root, out var innerSource))
        {
            throw new ScryValidationException($"Unknown source '{join.Root}'.");
        }

        var innerType = innerSource.ClrType;
        var inner = ApplyPolicy(innerSource.Resolve(db, scope.Services), innerSource, db, scope);

        if (join.InnerOps is { } innerOps)
        {
            inner = ApplySideOps(builder, inner, innerOps, innerType);
        }
        else if (join.InnerPredicate is { } predicate)
        {
            inner = Apply(inner, "Where", builder.BuildPredicate(predicate, innerType));
        }

        var (outerKey, innerKey) = builder.BuildJoinKeys(join.OuterKey, outerType, join.InnerKey, innerType);
        var (selector, shape, binarySlots) = builder.BuildJoinProjection(join.Result, outerType, innerType, join.Kind);

        var joined = (IQueryable<object[]>)outer.Provider.CreateQuery(
            CallQueryable(
                JoinMethod(join.Kind),
                [outerType, innerType, outerKey.ReturnType, typeof(object[])],
                outer.Expression,
                inner.Expression,
                Expression.Quote(outerKey),
                Expression.Quote(innerKey),
                Expression.Quote(selector)));

        return (joined, new(selector, shape, binarySlots));
    }

    /// <summary>
    /// Applies the pipeline a join's inner side or a set operand carries — filters, then an ordering
    /// bounded by paging — to that side's own query, before the two sides meet. The validator has
    /// already pinned the grammar, so anything else arriving here is a defect rather than a request.
    /// </summary>
    static IQueryable ApplySideOps(ExpressionBuilder builder, IQueryable side, IReadOnlyList<QueryOp> ops, Type elementType)
    {
        foreach (var op in ops)
        {
            side = op switch
            {
                WhereOp where => Apply(side, "Where", builder.BuildPredicate(where.Predicate, elementType)),
                OrderByOp orderBy => ApplyOrder(side, builder.BuildKeySelector(orderBy.Key, elementType), orderBy.Descending, then: false),
                ThenByOp thenBy => ApplyOrder(side, builder.BuildKeySelector(thenBy.Key, elementType), thenBy.Descending, then: true),
                SkipOp skip => ApplyPaging(side, "Skip", skip.Count),
                TakeOp take => ApplyPaging(side, "Take", take.Count),
                _ => throw new ScryValidationException($"'{op.GetType().Name}' is not allowed on the side of a join or set operation.")
            };
        }

        return side;
    }

    static string JoinMethod(JoinKind kind) =>
        kind switch
        {
            JoinKind.Inner => "Join",
            JoinKind.Left => "LeftJoin",
            JoinKind.Right => "RightJoin",
            JoinKind.Group => "GroupJoin",
            _ => throw new ScryValidationException($"Unsupported join kind '{kind}'.")
        };

    static Node? TerminalPredicate(QueryOp? terminal) =>
        terminal switch
        {
            CountOp count => count.Predicate,
            LongCountOp longCount => longCount.Predicate,
            AnyOp any => any.Predicate,
            FirstOp first => first.Predicate,
            SingleOp single => single.Predicate,
            LastOp last => last.Predicate,
            _ => null
        };

    /// <summary>
    /// Folds the whole sequence to one scalar. The selected values are projected first so the provider
    /// picks its aggregate overload from the value type, which <see cref="ExpressionBuilder"/> has
    /// already widened to one that exists.
    /// </summary>
    static Fold Aggregate(ExpressionBuilder builder, IQueryable query, AggregateOp aggregate, Type elementType)
    {
        var selector = builder.BuildAggregateSelector(aggregate.Selector, elementType, aggregate.Function);
        var values = ApplySelectTyped(query, selector);
        var name = aggregate.Function.ToString();

        // Min/Max are generic in the value type; Sum/Average have one overload per numeric type.
        MethodCallExpression call;
        if (aggregate.Function is AggregateFn.Min or AggregateFn.Max)
        {
            call = Expression.Call(typeof(Queryable), name, [values.ElementType], values.Expression);
        }
        else
        {
            call = Expression.Call(typeof(Queryable), name, null, values.Expression);
        }

        return new(values, call, null, null);
    }

    /// <summary>
    /// Translates every navigation into a policied source once, at startup. Runs through the same
    /// resolution a request does, so what is probed is what will execute.
    /// </summary>
    internal void ProbeNavigationPolicies(DbContext db, IServiceProvider services)
    {
        var scope = new CallScope(services, new HeaderDictionary(), new HeaderDictionary());
        NavigationPolicyProbe.Run(schema, db.Model, (name, include) => ResolveSource(name, db, scope, include), db);
    }

    IQueryable ResolveSource(string name, DbContext db, CallScope scope, Func<PolicyUse, bool>? include = null)
    {
        if (!schema.TryGetSource(name, out var source))
        {
            throw new ScryValidationException($"Unknown source '{name}'.");
        }

        return ApplyPolicy(source.Resolve(db, scope.Services), source, db, scope, include);
    }

    /// <summary>
    /// Fetches one attachment's bytes by its row's key. Shaped like a query and validated like one —
    /// the source and member are resolved through the allow-list, the key values are parsed into the
    /// key members' own types and bound as parameters — but it reads a single column of a single row
    /// rather than running a pipeline, since none arrives.
    /// </summary>
    public ScryAttachmentResult FetchAttachment(AttachmentRequest request, DbContext db, CallScope scope)
    {
        if (PlanAttachment(request, db, scope) is not { } fetch)
        {
            return ScryAttachmentResult.NotFound;
        }

        scope.Cached.Refresh();
        return fetch.Finish(Execution.Run(fetch.Rows, fetch.Call));
    }

    public async ValueTask<ScryAttachmentResult> FetchAttachmentAsync(AttachmentRequest request, DbContext db, CallScope scope, Cancel cancel)
    {
        if (PlanAttachment(request, db, scope) is not { } fetch)
        {
            return ScryAttachmentResult.NotFound;
        }

        await scope.Cached.RefreshAsync(cancel);
        return fetch.Finish(await Execution.RunAsync(fetch.Rows, fetch.Call, cancel));
    }

    /// <summary>
    /// An attachment read, unrun: the one-column query and the call that reads its one row, with what
    /// the model or the policy said the bytes are. Null where the request was refused or names a row
    /// that cannot exist, which answer alike so a guessed key learns nothing.
    /// </summary>
    readonly record struct AttachmentFetch(IQueryable Rows, MethodCallExpression Call, string? ContentType)
    {
        public ScryAttachmentResult Finish(object? row) =>
            row is object[] found
                ? new()
                {
                    Found = true,
                    Value = (byte[]?)found[0],
                    ContentType = ContentType
                }
                : ScryAttachmentResult.NotFound;
    }

    AttachmentFetch? PlanAttachment(AttachmentRequest request, DbContext db, CallScope scope)
    {
        if (request.Version > AttachmentRequest.CurrentVersion)
        {
            throw new ScryValidationException($"Unsupported attachment request version {request.Version}; this server supports up to {AttachmentRequest.CurrentVersion}.");
        }

        // An attachment is a row read like any other, so the policies deciding whether it is readable
        // answer from decisions as current as a query's would be.
        scope = scope with {EnsureCachedFreshness = true};

        if (!schema.TryGetSource(request.Root, out var source))
        {
            throw new ScryValidationException($"Unknown source '{request.Root}'.");
        }

        if (!schema.TryGetType(source.ClrType, out var meta) ||
            !meta.TryGetMember(request.Member, out var member) ||
            member.Kind != MemberKind.Attachment)
        {
            throw new ScryValidationException($"'{request.Member}' is not an attachment member of '{request.Root}'.");
        }

        // Non-null because the member above is an attachment, which is what makes the schema derive one.
        var keys = meta.AttachmentKeys!;
        if (keys.Count != request.Keys.Count)
        {
            throw new ScryValidationException($"'{request.Root}' is keyed by {keys.Count} value(s); the request carried {request.Keys.Count}.");
        }

        var builder = new ExpressionBuilder(schema, options);
        var values = new List<object>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            // A primary key is never null, so a null key value cannot match a row. Answered as
            // not-found rather than rejected: it is a key that identifies nothing, not a malformed one.
            if (request.Keys[i].Value is not { } value)
            {
                return null;
            }

            values.Add(builder.ParseKey(value, keys[i].Type));
        }

        // Before the database is touched: an unauthorized caller learns nothing, not even how long a
        // lookup took.
        var policy = source.AttachmentPolicy ??
                     throw new($"Source '{request.Root}' exposes an attachment with no policy to authorize it.");
        var context = new ScryAttachmentContext(scope.Services, db, member.Name, values, scope.RequestHeaders, scope.ResponseHeaders)
        {
            // The model's declaration, which the policy may replace where the row rather than the
            // member decides what its bytes are.
            ContentType = member.ContentType
        };
        if (!AttachmentPolicy.Authorize(policy, scope.Services, context))
        {
            return null;
        }

        // Resolved through the same path a query's root takes, so the source's row policies apply and
        // a row a query could not have returned is not one an attachment can be pulled from.
        var query = ResolveSource(request.Root, db, scope);
        var parameter = Expression.Parameter(source.ClrType, "_");
        Expression? predicate = null;
        for (var i = 0; i < keys.Count; i++)
        {
            var comparison = Expression.Equal(
                Expression.Property(parameter, keys[i].Property),
                Expression.Convert(Parameterization.Parameterize(values[i], values[i].GetType()), keys[i].Type));
            predicate = predicate is null ? comparison : Expression.AndAlso(predicate, comparison);
        }

        query = Apply(query, "Where", Expression.Lambda(predicate!, parameter));

        // Only the one column is selected: the row may be wide, and nothing else about it is being
        // asked for. Projected into object[] — the shape the rest of the executor reads — so a row
        // holding a null value stays distinguishable from no row at all.
        var selector = Expression.Lambda(
            Expression.NewArrayInit(typeof(object), Expression.Convert(Expression.Property(parameter, member.Property), typeof(object))),
            parameter);
        var rows = ApplySelect(query, selector);
        return new(rows, CallQueryable("SingleOrDefault", [typeof(object[])], rows.Expression), context.ContentType);
    }

    static (IQueryable<object[]> Query, ProjectionPlan Plan) BuildProjected(
        ExpressionBuilder builder,
        IQueryable query,
        Type elementType,
        GroupByOp? groupBy,
        SelectOp? select,
        Node? groupFilter)
    {
        if (groupBy is not null)
        {
            var keySelector = builder.BuildGroupKeySelector(groupBy.Keys, elementType);
            var keyType = keySelector.ReturnType;
            var grouped = ApplyGroupBy(query, keySelector, elementType, keyType);
            if (groupFilter is not null)
            {
                grouped = Apply(grouped, "Where", builder.BuildGroupPredicate(groupFilter, elementType, keyType));
            }

            var groupPlan = builder.BuildGroupProjection(select!.Projection, elementType, keyType);
            return (ApplySelect(grouped, groupPlan.Selector), groupPlan);
        }

        var plan = select is null
            ? builder.BuildDefaultProjection(elementType)
            : builder.BuildProjection(select.Projection, elementType);
        return (ApplySelect(query, plan.Selector), plan);
    }

    // The type a policy filters and its IReturnablePolicy<T>.Filter, keyed by the policy type — not by
    // the source's, which is a different type whenever the policy is inherited. Bounded by the schema.
    static readonly ConcurrentDictionary<Type, (Type EntityType, MethodInfo Filter)> policyFilters = new();

    /// <summary>
    /// Applies every policy the source carries, base-most first, so the rows a client can go on to
    /// filter are those all of them allow. A policy declared on a base filters that base's rows, so the
    /// query is widened to the type the policy was written against and narrowed back afterwards.
    /// </summary>
    static IQueryable ApplyPolicy(IQueryable query, ScrySource source, DbContext db, CallScope scope, Func<PolicyUse, bool>? include = null) =>
        ApplyPolicies(query, source, 0, db, scope, include);

    /// <summary>
    /// The same, for a query that already carries the first <paramref name="from"/> of the source's
    /// policies. A source's chain extends its base's rather than replacing it, so narrowing to a
    /// subclass leaves only the levels below the base still to apply — and a policy is a filter to
    /// apply once, not one to repeat per narrowing.
    /// </summary>
    /// <remarks>
    /// <paramref name="include"/> selects a subset of the chain, which is how the denied-row probe
    /// builds the same query minus the policies that fail rather than hide: everything a caller may see
    /// once the hiding policies have run. Null applies the whole chain, which is every real query.
    /// </remarks>
    static IQueryable ApplyPolicies(
        IQueryable query,
        ScrySource source,
        int from,
        DbContext db,
        CallScope scope,
        Func<PolicyUse, bool>? include = null)
    {
        if (source.Policies.Count == from)
        {
            return Retype(query, source.ClrType);
        }

        var context = new ScryPolicyContext(scope.Services, db, scope.RequestHeaders, scope.ResponseHeaders);
        foreach (var use in source.Policies.Skip(from))
        {
            if (include is not null &&
                !include(use))
            {
                continue;
            }

            var policy = use.Instance ?? scope.Services.GetService(use.Policy) ?? Activator.CreateInstance(use.Policy);
            if (policy is null)
            {
                throw new($"Could not create policy '{use.Policy.Name}'.");
            }

            var (entityType, filter) = policyFilters.GetOrAdd(use.Policy, PolicyFilter);

            // A cached policy is applied directly rather than through the interface: what it needs
            // beyond the context — where to remember this call's answer, and whether it may decide any
            // rows — belongs to the call, not to the policy, and hanging it off the context would put
            // it in front of every host-written policy too.
            query = policy is ICachedPolicyAdapter cached
                ? cached.Filter(Retype(query, entityType), context, scope.Cached, scope.EnsureCachedFreshness)
                : (IQueryable)filter.Invoke(policy, [Retype(query, entityType), context])!;
        }

        return Retype(query, source.ClrType);
    }

    /// <summary>
    /// The denied-row question for a source a query reads beside its root — a join's inner side, a
    /// set operand, a membership set. Each reads that source as a list, so its <c>RootList</c> mode
    /// applies: where a policy there reports denials, the source is probed before this query runs,
    /// narrowed by <paramref name="narrow"/>, the filters the side carries. Its ordering and paging
    /// are left out, as the root's are — a denied row beyond the page is still one the query asked for.
    /// </summary>
    static DeniedRowProbe? ProbeSideSource(ScrySource source, DbContext db, CallScope scope, Func<IQueryable, IQueryable>? narrow = null)
    {
        if (!source.Policies.Any(_ => _.Errors(DeniedPosition.RootList)))
        {
            return null;
        }

        IQueryable Side(Func<PolicyUse, bool>? include)
        {
            var side = ApplyPolicy(source.Resolve(db, scope.Services), source, db, scope, include);
            if (narrow is null)
            {
                return side;
            }

            return narrow(side);
        }

        return DeniedRowProbe.Rows(Side(use => !use.Errors(DeniedPosition.RootList)), Side(include: null), db);
    }

    // The filters a side pipeline carries — the operators before its ordering and paging, which the
    // validator has pinned to come first — or the one predicate the older shape carries instead.
    static IQueryable SideFilters(ExpressionBuilder builder, IQueryable side, Type elementType, IReadOnlyList<QueryOp>? ops, Node? predicate)
    {
        if (ops is not null)
        {
            foreach (var where in ops.OfType<WhereOp>())
            {
                side = Apply(side, "Where", builder.BuildPredicate(where.Predicate, elementType));
            }

            return side;
        }

        if (predicate is null)
        {
            return side;
        }

        return Apply(side, "Where", builder.BuildPredicate(predicate, elementType));
    }

    /// <summary>
    /// The question that fails the request where a policy configured to error denied a row this query
    /// would otherwise have read, or null where no policy in the chain says so — which is every query
    /// until a host asks for one, and costs nothing.
    /// </summary>
    static DeniedRowProbe? ProbeDeniedRows(ScrySource source, ProbeSteps steps, QueryOp? terminal, DbContext db, CallScope scope)
    {
        // A single-row terminal answers with the row itself; everything else lists rows or folds them,
        // and a policy can want a denial to fail one and not the other.
        var position = terminal is FirstOp or SingleOp or LastOp
            ? DeniedPosition.RootSingle
            : DeniedPosition.RootList;

        if (!steps.Sources(source).Any(_ => _.Policies.Any(policy => policy.Errors(position))))
        {
            return null;
        }

        return DeniedRowProbe.Rows(
            ProbeRoot(source, steps, use => !use.Errors(position), db, scope),
            ProbeRoot(source, steps, include: null, db, scope),
            db);
    }

    /// <summary>
    /// Rebuilds the rows the query read over a root carrying only the policies <paramref name="include"/>
    /// keeps, by replaying what the fold recorded.
    /// </summary>
    static IQueryable ProbeRoot(ScrySource source, ProbeSteps steps, Func<PolicyUse, bool>? include, DbContext db, CallScope scope)
    {
        var query = ApplyPolicy(source.Resolve(db, scope.Services), source, db, scope, include);
        foreach (var step in steps.Recorded)
        {
            if (step.Where is { } predicate)
            {
                query = Apply(query, "Where", predicate);
                continue;
            }

            // Narrowed exactly as the fold narrowed it, the same levels of the chain skipped as already
            // applied, so the two builds differ in nothing but which policies they carry.
            var derived = step.Narrow!;
            query = query.Provider.CreateQuery(
                CallQueryable("OfType", [derived.ClrType], query.Expression));
            query = ApplyPolicies(query, derived, step.NarrowFrom, db, scope, include);
        }

        return query;
    }

    static (Type EntityType, MethodInfo Filter) PolicyFilter(Type policyType)
    {
        var entityType = RowPolicy.EntityType(policyType);
        return (entityType, typeof(IReturnablePolicy<>)
            .MakeGenericType(entityType)
            .GetMethod(nameof(IReturnablePolicy<>.Filter))!);
    }

    /// <summary>
    /// Moves a query between two types of one hierarchy. Widening is a Cast — every row already is one
    /// — and narrowing is an OfType, which the discriminator answers. Both are no-ops when the types
    /// already agree, which is every source whose policies are all its own.
    /// </summary>
    static IQueryable Retype(IQueryable query, Type target)
    {
        if (query.ElementType == target)
        {
            return query;
        }

        var method = target.IsAssignableFrom(query.ElementType) ? "Cast" : "OfType";
        return query.Provider.CreateQuery(CallQueryable(method, [target], query.Expression));
    }

    static IQueryable Apply(IQueryable query, string method, LambdaExpression argument) =>
        query.Provider.CreateQuery(
            CallQueryable(method, [query.ElementType], query.Expression, Expression.Quote(argument)));

    static IQueryable ApplyOrder(IQueryable query, LambdaExpression keySelector, bool descending, bool then)
    {
        var method = (then ? "ThenBy" : "OrderBy") + (descending ? "Descending" : "");
        return query.Provider.CreateQuery(
            CallQueryable(
                method,
                [query.ElementType, keySelector.ReturnType],
                query.Expression,
                Expression.Quote(keySelector)));
    }

    // The count is bound rather than inlined so every offset and limit shares one statement — and so
    // one compiled plan serves every page a client walks.
    static IQueryable ApplyPaging(IQueryable query, string method, int count) =>
        query.Provider.CreateQuery(
            CallQueryable(method, [query.ElementType], query.Expression, Parameterization.Parameterize(count, typeof(int))));

    static IQueryable ApplyGroupBy(IQueryable query, LambdaExpression keySelector, Type elementType, Type keyType) =>
        query.Provider.CreateQuery(
            CallQueryable("GroupBy", [elementType, keyType], query.Expression, Expression.Quote(keySelector)));

    static IQueryable<object[]> ApplySelect(IQueryable query, LambdaExpression selector) =>
        (IQueryable<object[]>)query.Provider.CreateQuery(
            CallQueryable("Select", [query.ElementType, typeof(object[])], query.Expression, Expression.Quote(selector)));

    // Which projected leaf an ordering names. The validator has already established that the key names
    // one of them and that none of them is nested, so member position is leaf position.
    static int DistinctLeaf(Projection projection, Node key)
    {
        if (key is MemberNode { Path: [var name] })
        {
            for (var i = 0; i < projection.Members.Count; i++)
            {
                if (string.Equals(projection.Members[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        throw new ScryValidationException("An ordering after Distinct must name a projected member.");
    }

    static IQueryable ApplySelectTyped(IQueryable query, LambdaExpression selector) =>
        query.Provider.CreateQuery(
            CallQueryable("Select", [query.ElementType, selector.ReturnType], query.Expression, Expression.Quote(selector)));

    /// <summary>
    /// Deduplicates the projected rows. A relational provider turns this into <c>SELECT DISTINCT</c>
    /// over the projected columns. An in-memory POCO source runs the same operator under LINQ to
    /// Objects, where <c>object[]</c> compares by reference and nothing would ever match, so those
    /// compare the row values instead.
    /// </summary>
    static IQueryable<object[]> ApplyDistinct(IQueryable<object[]> query, SourceKind kind)
    {
        if (kind == SourceKind.Poco)
        {
            return query.Distinct(RowComparer.Instance);
        }

        return (IQueryable<object[]>)query.Provider.CreateQuery(
            CallQueryable("Distinct", [typeof(object[])], query.Expression));
    }

    sealed class RowComparer :
        IEqualityComparer<object[]>
    {
        public static readonly RowComparer Instance = new();

        public bool Equals(object[]? x, object[]? y) =>
            x is not null &&
            y is not null &&
            x.Length == y.Length &&
            x.Zip(y).All(_ => Equals(_.First, _.Second));

        public int GetHashCode(object[] row)
        {
            var hash = new HashCode();
            foreach (var value in row)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    static readonly ConcurrentDictionary<string, MethodInfo> queryableMethods = new();

    /// <summary>
    /// Builds a call to a generic <see cref="Queryable"/> method. Each method name is used with exactly
    /// one overload shape here, so the first call lets the framework's name-based binder pick the right
    /// overload, then caches its open generic definition. Later calls close that definition over
    /// <paramref name="typeArgs"/> and bind directly — skipping the metadata scan and overload
    /// resolution the name-based <see cref="Expression.Call(Type, string, Type[], Expression[])"/> does
    /// on every invocation.
    /// </summary>
    static MethodCallExpression CallQueryable(string method, Type[] typeArgs, params Expression[] arguments)
    {
        if (queryableMethods.TryGetValue(method, out var open))
        {
            return Expression.Call(open.MakeGenericMethod(typeArgs), arguments);
        }

        var call = Expression.Call(typeof(Queryable), method, typeArgs, arguments);
        queryableMethods.TryAdd(method, call.Method.GetGenericMethodDefinition());
        return call;
    }

    PagePlan Page(
        ExpressionBuilder builder,
        IQueryable query,
        Type elementType,
        SelectOp? select,
        IReadOnlyList<(Node Key, bool Descending)> orderings,
        bool seekEligible,
        PageOp page,
        ScrySource source,
        DbContext db,
        BinaryPartCollector? binary)
    {
        var size = Math.Min(page.Size ?? options.DefaultPageSize, options.MaxPageSize);

        var (seekSafe, keys) = PlanSeek(orderings, seekEligible, source, elementType, db);
        if (!seekSafe && page.Cursor is not null)
        {
            throw new ScryValidationException(
                "Cursor paging requires an ordered query over an entity with non-nullable ordering keys.");
        }

        // Stamped once the tiebreaker is known, so it describes the order actually seeked rather than
        // only what the client wrote.
        string? order = null;
        if (seekSafe)
        {
            // Append the primary key as a trailing ascending tiebreaker so the order — and the cursor
            // that resumes it — is total.
            foreach (var (key, _) in keys.Skip(orderings.Count))
            {
                query = ApplyOrder(query, builder.BuildKeySelector(key, elementType), descending: false, then: true);
            }

            order = CursorCodec.OrderStamp(source.Name, keys);

            if (page.Cursor is not null)
            {
                var (values, cursorOrder) = CursorCodec.Decode(page.Cursor, SigningKey());

                // The whole guard: a cursor resumes the ordering it was issued for, or nothing. Without
                // this, an ordering of the same shape — a flipped direction, another column of the same
                // type — seeks happily against values that describe a different sequence and answers
                // with a plausible, silently wrong page. It also subsumes the key-count check, since a
                // different number of keys stamps differently.
                if (cursorOrder != order)
                {
                    throw new ScryValidationException(
                        "Paging cursor does not match the query's ordering. A cursor resumes the ordering it was issued for; re-request the first page after changing the sort.");
                }

                query = Apply(query, "Where", builder.BuildSeekPredicate(keys, values, elementType));
            }
        }

        var effectiveKeys = seekSafe ? keys : Array.Empty<(Node Key, bool Descending)>();
        var (selector, shape, binarySlots, keyCount) = builder.BuildPageProjection(select?.Projection, effectiveKeys, elementType);
        var projected = ApplySelect(query, selector);
        var plan = new ProjectionPlan(selector, shape, binarySlots);

        // Fetch one extra row to detect a further page without issuing a second COUNT query. Composed
        // through ApplyPaging rather than Queryable.Take so the count is bound, not inlined.
        var fetch = (IQueryable<object[]>)ApplyPaging(projected, "Take", size + 1);

        PageSet Finish(List<object[]> rows)
        {
            var hasMore = rows.Count > size;
            if (hasMore)
            {
                rows.RemoveRange(size, rows.Count - size);
            }

            // The next cursor is the ordering-key tuple of the last returned row — omitted on the last
            // page (nothing more to resume) and when the query is not seek-safe (offset paging only).
            // Read off the rows before they are shaped, since the key columns sit past the projected
            // ones.
            string? cursor = null;
            if (seekSafe &&
                hasMore &&
                rows.Count > 0)
            {
                var last = rows[^1];
                var keyValues = new (string?, ClrTypeTag)[keyCount];
                for (var i = 0; i < keyCount; i++)
                {
                    keyValues[i] = CursorCodec.TagValue(last[shape.Count + i]);
                }

                cursor = CursorCodec.Encode(keyValues, order!, SigningKey());
            }

            return new(rows, plan, hasMore, cursor, binary);
        }

        return new(fetch, Finish);
    }

    // Decides whether a page can be resumed by a keyset cursor and, if so, the total ordering to seek
    // over: the client's ordering keys plus the primary key appended as a tiebreaker. A cursor is only
    // safe over an entity ordered by single-segment, non-nullable scalar members (so the seek is a true
    // total order); anything else — a view/POCO, no ordering, a nav-path or nullable key — falls back to
    // offset paging with no cursor.
    (bool SeekSafe, IReadOnlyList<(Node Key, bool Descending)> Keys) PlanSeek(
        IReadOnlyList<(Node Key, bool Descending)> orderings,
        bool seekEligible,
        ScrySource source,
        Type elementType,
        DbContext db)
    {
        if (!seekEligible ||
            source.Kind != SourceKind.Entity ||
            orderings.Count == 0)
        {
            return (false, orderings);
        }

        var primaryKey = db.Model.FindEntityType(elementType)?.FindPrimaryKey();
        if (primaryKey is null ||
            !schema.TryGetType(elementType, out var meta))
        {
            return (false, orderings);
        }

        // Every key, the tiebreaker included, has to be one the next page can seek past: a cursor
        // over a key the seek predicate cannot compare would be issued whole and fault on its return.
        foreach (var (key, _) in orderings)
        {
            if (key is not MemberNode { Path: [var single] } ||
                !IsNonNullableScalar(db, elementType, single) ||
                !meta.Members.TryGetValue(single, out var ordered) ||
                !ExpressionBuilder.CanSeek(ordered.Type))
            {
                return (false, orderings);
            }
        }

        var pkKeys = new List<(Node, bool)>(primaryKey.Properties.Count);
        foreach (var property in primaryKey.Properties)
        {
            // The key must be an exposable scalar to build a member node for the tiebreaker/cursor.
            if (!meta.Members.TryGetValue(property.Name, out var member) ||
                member.Kind != MemberKind.Scalar ||
                !ExpressionBuilder.CanSeek(member.Type))
            {
                return (false, orderings);
            }

            if (!orderings.Any(_ => _.Key is MemberNode { Path: [var name] } &&
                                    name == property.Name))
            {
                pkKeys.Add((new MemberNode([property.Name]), false));
            }
        }

        return (true, [.. orderings, .. pkKeys]);
    }

    static bool IsNonNullableScalar(DbContext db, Type elementType, string member) =>
        db.Model.FindEntityType(elementType)?.FindProperty(member) is { IsNullable: false };

    byte[] SigningKey() =>
        options.CursorKey ?? ephemeralSigningKey;

    // Used when no CursorKey is configured: cursors are valid only within this process's lifetime.
    static readonly byte[] ephemeralSigningKey = RandomNumberGenerator.GetBytes(32);

    static Terminal Scalar(object? value) =>
        new(ResultKind.Scalar, value, Row: null, Plan: null, Binary: null);

    static Terminal Single(object[]? row, ProjectionPlan plan, BinaryPartCollector? binary) =>
        new(ResultKind.Single, Value: null, row, plan, binary);

    /// <summary>
    /// A terminal's response the general way: the value through the serializer, or the row shaped into
    /// a dictionary and serialized. <c>ResponseWriter.WriteTerminal</c> writes the same bytes straight,
    /// and the golden tests hold the two together.
    /// </summary>
    /// <remarks>
    /// A scalar is serialized as <see cref="object"/> rather than the static type it was executed as,
    /// which the serializer answers by its runtime type — the same converter, and so the same bytes.
    /// </remarks>
    static QueryResponse Materialize(Terminal terminal)
    {
        if (terminal.Kind == ResultKind.Scalar)
        {
            return QueryResponse.Create(
                ResultKind.Scalar,
                JsonSerializer.SerializeToElement(terminal.Value, ScryJson.Options));
        }

        var payload = terminal.Row is { } row
            ? JsonSerializer.SerializeToElement(Shape(row, terminal.Plan!, terminal.Binary), ScryJson.Options)
            : JsonSerializer.SerializeToElement<object?>(null);
        return QueryResponse.Create(ResultKind.Single, payload);
    }

    static Dictionary<string, object?> Shape(object[] row, ProjectionPlan plan, BinaryPartCollector? binary)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Shape.Count; i++)
        {
            var path = plan.Shape[i];
            var node = root;
            for (var segment = 0; segment < path.Count - 1; segment++)
            {
                if (node.TryGetValue(path[segment], out var existing) &&
                    existing is Dictionary<string, object?> childExisting)
                {
                    node = childExisting;
                }
                else
                {
                    var child = new Dictionary<string, object?>(StringComparer.Ordinal);
                    node[path[segment]] = child;
                    node = child;
                }
            }

            // A binary slot's value leaves as a part and a placeholder holds its position; serialized
            // through ScryJson the placeholder's bytes match the fast writer's hand-written form.
            node[path[^1]] = binary is not null &&
                             plan.BinarySlots?[i] == true &&
                             row[i] is byte[] bytes
                ? new BinaryPlaceholder(binary.Add(bytes))
                : row[i];
        }

        return root;
    }
}
