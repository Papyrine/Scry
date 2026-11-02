/// <summary>
/// Reads which entities a query reads, from the query as it will actually run — after the policies
/// have been applied and every navigation rebound — so a table only a policy names counts as much as
/// one the client named. What comes back is what a live query listens for; null means it could not be
/// told, and the query listens for everything.
/// </summary>
/// <remarks>
/// <para>
/// Null is the safe answer and is given freely: a view or a keyless type, whose rows come from tables
/// the model does not name; a POCO source, whose rows come from nowhere EF can see; a query root this
/// does not recognise — raw SQL is one, and belongs to a package this assembly does not reference; and
/// anything that throws. A query that listens for everything is only ever run more often than it
/// needed to be.
/// </para>
/// <para>
/// A global query filter is not in the expression either — EF applies it as it compiles the query,
/// after this has read it — so each type's filters are read here from the model, as the query will
/// have them: a filter that reaches another type through a navigation, or reads another set outright,
/// makes that type one the query depends on.
/// </para>
/// <para>
/// What this cannot see is what is not in the expression: a policy that loaded a list in C# and
/// filters by it, or answers by a claim or the clock. Nothing reported reaches those, which is what a
/// live query's poll is for.
/// </para>
/// </remarks>
sealed class DependencyWalker(IModel model) :
    ExpressionVisitor
{
    HashSet<string> names = [with(StringComparer.Ordinal)];
    bool unknown;

    // The types whose filters have been read, so a filter on one type reaching another whose filter
    // reaches the first is read once each rather than for ever.
    HashSet<IReadOnlyEntityType> filtered = [];

    public static IReadOnlySet<string>? Read(IModel model, IEnumerable<Expression?> expressions)
    {
        try
        {
            var walker = new DependencyWalker(model);
            foreach (var expression in expressions)
            {
                walker.Visit(expression);
            }

            if (walker.unknown ||
                walker.names.Count == 0)
            {
                return null;
            }

            return walker.names;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [return: NotNullIfNotNull(nameof(node))]
    public override Expression? Visit(Expression? node)
    {
        // A set reached some other way than as a query root: a policy that captured db.Grants in a
        // closure, or a filter reading another of the context's sets, shows up as a member typed
        // DbSet<Grant>, which EF only turns into a root later.
        if (node is not null &&
            node is not QueryRootExpression &&
            ElementOfQueryable(node.Type) is { } element)
        {
            var mapped = false;
            foreach (var type in model.FindEntityTypes(element))
            {
                Add(type);
                mapped = true;
            }

            // A query held in a field and typed over a shape no entity has — a projection built ahead
            // of time — reads tables its tree would name, and its tree is not here to be read.
            if (!mapped &&
                node is MemberExpression)
            {
                unknown = true;
            }
        }

        return base.Visit(node);
    }

    protected override Expression VisitExtension(Expression node)
    {
        if (node is QueryRootExpression root)
        {
            // Exactly an entity root, or not something this can speak for. Raw SQL and table-valued
            // functions are subclasses, and read whatever their text says.
            if (root is EntityQueryRootExpression entity &&
                root.GetType() == typeof(EntityQueryRootExpression))
            {
                Add(entity.EntityType);
            }
            else
            {
                unknown = true;
            }

            return node;
        }

        return base.VisitExtension(node);
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        if (node.Value is IQueryable queryable)
        {
            // A query held as a value: an EF one carries its own tree, which is walked; anything else
            // is rows in memory — a POCO source — that nothing here can watch.
            if (queryable.Expression is ConstantExpression)
            {
                if (!model.FindEntityTypes(queryable.ElementType).Any())
                {
                    unknown = true;
                }
            }
            else
            {
                Visit(queryable.Expression);
            }
        }

        return base.VisitConstant(node);
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression is { } owner)
        {
            foreach (var type in model.FindEntityTypes(owner.Type))
            {
                if (type.FindNavigation(node.Member.Name) is { } navigation)
                {
                    Add(navigation.TargetEntityType);
                }

                // A many-to-many is read through its join table, whose rows change when the
                // relationship does and neither end has.
                if (type.FindSkipNavigation(node.Member.Name) is { } skip)
                {
                    Add(skip.TargetEntityType);
                    Add(skip.JoinEntityType);
                }
            }
        }

        return base.VisitMember(node);
    }

    void Add(IReadOnlyEntityType type)
    {
        if (DerivedElsewhere(type))
        {
            unknown = true;
            return;
        }

        names.Add(EntityNames.Root(type));
        Filters(type.GetRootType());
    }

    // What EF adds to every query of the type as it compiles it, read off the root: a filter may only
    // be declared there, and every type under it is read through it.
    void Filters(IReadOnlyEntityType root)
    {
        if (!filtered.Add(root))
        {
            return;
        }

        foreach (var filter in root.GetDeclaredQueryFilters())
        {
            Visit(filter.Expression);
        }
    }

    // Read by name: the mapping annotations are the relational package's, which this assembly does not
    // reference. Any of them means the rows come from somewhere other than this type's own table.
    static bool DerivedElsewhere(IReadOnlyEntityType type) =>
        type.FindPrimaryKey() is null ||
        type.FindAnnotation(viewName)?.Value is not null ||
        type.FindAnnotation(sqlQuery)?.Value is not null ||
        type.FindAnnotation(functionName)?.Value is not null;

    // Asked of every node of every run, and answered by reflecting over the type's interfaces, so
    // the answer is kept, for a bounded number of types: a node can be closed over a row type the
    // request chose.
    static BoundedTypeCache<Type?> elements = new(1024);

    static Type? ElementOfQueryable(Type type) =>
        elements.GetOrAdd(type, FindElement);

    static Type? FindElement(Type type)
    {
        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(IQueryable<>))
        {
            return type.GetGenericArguments()[0];
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType &&
                contract.GetGenericTypeDefinition() == typeof(IQueryable<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return null;
    }

    const string viewName = "Relational:ViewName";
    const string sqlQuery = "Relational:SqlQuery";
    const string functionName = "Relational:FunctionName";
}
