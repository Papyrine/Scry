/// <summary>
/// What the disclosure audit learns about a query while it is rebound: every member it read and what
/// it did with each, and which rows each row of the answer will have been read from. One per request,
/// handed to the <see cref="ExpressionBuilder"/> that does the rebinding; absent where the audit is
/// off, which is the one check the builder makes.
/// </summary>
/// <remarks>
/// <para>
/// A member is named by the source whose row it is a value of and its path from that row, so a
/// member of a complex type is recorded against the entity that holds it — <c>Employee</c>,
/// <c>Address.City</c> — and a path that crosses into another source is recorded as a step on the
/// first and a member of the second.
/// </para>
/// <para>
/// A row of the answer is identified by keys the selector reads beside what was asked for, into
/// slots after the ones that are written. They are read from the database with the row and never
/// serialized: both writers go by the plan's shape, which does not know they are there.
/// </para>
/// </remarks>
sealed class DisclosurePlanner(Schema schema, IModel model, ScryDisclosureOptions settings, bool inMemory)
{
    /// <summary>
    /// Where an expression stands relative to the rows of the answer: whose member a read off it is,
    /// and — where it is a row reached from the answer's own — how it was reached.
    /// </summary>
    /// <param name="Source">The source whose row this is, or is part of.</param>
    /// <param name="Prefix">The path from that row to here, ending in a dot, or empty at the row itself.</param>
    /// <param name="Via">How the answer's row reaches that row: empty for the row itself.</param>
    /// <param name="Rooted">Whether this is reached from a row of the answer, rather than inside a subquery.</param>
    /// <param name="Row">The expression for the row itself, which its key is read off.</param>
    public readonly record struct Origin(string Source, string Prefix, string Via, bool Rooted, Expression Row);

    HashSet<ScryDisclosureField> fields = [];
    Dictionary<Expression, Origin> origins = [];
    Dictionary<Type, (string Source, string Prefix)> elements = [];
    Dictionary<Type, IReadOnlyList<PropertyInfo>?> keys = [];
    List<ScryDisclosureField>? capturing;
    IReadOnlyList<IReadOnlyList<ScryDisclosureField>> groupKeys = [];

    // The rows the projection now being built reaches, in the order they were first reached.
    List<(string Source, string Via, IReadOnlyList<Expression> Key)> reached = [];

    /// <summary>What a member read now is being read for.</summary>
    public ScryDisclosureFieldUse Use { get; set; } = ScryDisclosureFieldUse.Read;

    /// <summary>
    /// Set while the builder reads something the server added and the query did not ask for — a
    /// page's cursor keys and their tiebreak. Those are read, and are no part of what was asked.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>Whether the answer returns a member the model marks sensitive.</summary>
    public bool Sensitive =>
        fields.Any(_ => _.Sensitive && _.Use is ScryDisclosureFieldUse.Returned or ScryDisclosureFieldUse.Aggregated);

    /// <summary>
    /// Every member read, in an order that depends on nothing but what they are — so two requests of
    /// one shape describe themselves identically.
    /// </summary>
    public IReadOnlyList<ScryDisclosureField> Fields() =>
    [
        .. fields
            .OrderBy(_ => _.Source, StringComparer.Ordinal)
            .ThenBy(_ => _.Member, StringComparer.Ordinal)
            .ThenBy(_ => _.Use)
    ];

    /// <summary>Marks a lambda parameter as a row of the answer, reached as <paramref name="via"/>.</summary>
    public void Row(ParameterExpression row, string via) =>
        origins[row] = Start(row) with
        {
            Via = via,
            Rooted = true
        };

    /// <summary>Marks a lambda parameter as an element of the collection last read.</summary>
    public void Element(ParameterExpression element, (string Source, string Prefix) of) =>
        origins[element] = new(of.Source, of.Prefix, Via: "", Rooted: false, element);

    /// <summary>
    /// Says rows of a type are from here on the elements of a flattened collection, so a member read
    /// off one is named against the row that held the collection.
    /// </summary>
    public void Flattened(Type element, (string Source, string Prefix) of)
    {
        if (!schema.TryGetSourceForType(element, out _))
        {
            elements[element] = of;
        }
    }

    /// <summary>Where an expression a member path begins at stands.</summary>
    public Origin Start(Expression root)
    {
        if (origins.TryGetValue(root, out var known))
        {
            return known;
        }

        var type = Nullable.GetUnderlyingType(root.Type) ?? root.Type;
        if (elements.TryGetValue(type, out var element))
        {
            return new(element.Source, element.Prefix, Via: "", Rooted: false, root);
        }

        return new(SourceName(type), Prefix: "", Via: "", Rooted: false, root);
    }

    /// <summary>
    /// Records one step of a member path and answers where the path stands after it.
    /// </summary>
    /// <param name="origin">Where the path stood before the step.</param>
    /// <param name="owner">The type the member was resolved on.</param>
    /// <param name="meta">That type's metadata.</param>
    /// <param name="member">The member stepped onto.</param>
    /// <param name="value">The expression for the member's value.</param>
    /// <param name="last">Whether the path ends here.</param>
    public Origin Step(Origin origin, Type owner, TypeMeta meta, Member member, Expression value, bool last)
    {
        // A type that is a source in its own right is where its members are named from, however it
        // was come by: a row parameter, or the far end of a navigation.
        if (origin.Prefix.Length == 0 &&
            schema.TryGetSourceForType(owner, out _))
        {
            origin = origin with
            {
                Source = SourceName(owner)
            };
        }

        var name = origin.Prefix + member.Name;
        var sensitive = member.Sensitive || meta.Sensitive;

        if (member.Kind == MemberKind.Capability)
        {
            // Recorded as itself where it is returned. What it was computed from is the command
            // policy's own condition, which is the host's code and is not walked.
            var use = Use;
            if (use == ScryDisclosureFieldUse.Returned)
            {
                use = ScryDisclosureFieldUse.Capability;
            }

            Record(origin, name, use, sensitive);
            return origin;
        }

        if (member.Kind == MemberKind.Navigation)
        {
            // A step to another row is recorded as a step, and what follows is that row's. A step
            // into a value the row holds is no step at all: what follows is still this row's.
            if (KeyOf(member.Target) is not null ||
                schema.TryGetSourceForType(member.Target, out _))
            {
                Record(origin, name, ScryDisclosureFieldUse.Traversed, sensitive);
                return Remember(
                    value,
                    new(SourceName(member.Target), Prefix: "", Via(origin.Via, member.Name), origin.Rooted, value));
            }

            return Remember(
                value,
                origin with
                {
                    Prefix = name + "."
                });
        }

        if (member.Element is { } element)
        {
            // A collection's own name is what a count of it or a test of it reads; its elements'
            // members are named beneath it, or as the rows of their own source that they are.
            var traversed = Use;
            if (!last)
            {
                traversed = ScryDisclosureFieldUse.Traversed;
            }

            Record(origin, name, traversed, sensitive);
            LastElement = Elements(origin, name, element);
            return origin;
        }

        var read = Use;
        if (!last)
        {
            read = ScryDisclosureFieldUse.Traversed;
        }

        Record(origin, name, read, sensitive);
        return origin;
    }

    /// <summary>
    /// Where the elements of the collection last stepped onto stand: what a lambda over them is told.
    /// </summary>
    public (string Source, string Prefix) LastElement { get; private set; }

    (string Source, string Prefix) Elements(Origin origin, string name, Type element)
    {
        if (schema.TryGetSourceForType(element, out _))
        {
            return (SourceName(element), "");
        }

        return (origin.Source, name + ".");
    }

    // Registered against the expression itself, so a caller that goes on reading members off what a
    // path returned — a nested projection — is found again by what it holds.
    Origin Remember(Expression value, Origin origin)
    {
        origins[value] = origin;
        return origin;
    }

    /// <summary>Carries an origin over to an expression that stands for the same value, widened.</summary>
    public void Same(Expression original, Expression widened)
    {
        if (!ReferenceEquals(original, widened) &&
            origins.TryGetValue(original, out var origin))
        {
            origins[widened] = origin;
        }
    }

    static string Via(string via, string member)
    {
        if (via.Length == 0)
        {
            return member;
        }

        return $"{via}.{member}";
    }

    void Record(Origin origin, string member, ScryDisclosureFieldUse use, bool sensitive)
    {
        if (Suspended)
        {
            return;
        }

        var field = new ScryDisclosureField(origin.Source, member, use, sensitive);
        fields.Add(field);
        capturing?.Add(field);

        // Something of another row left with this one, so that row is one the answer names: its key
        // is read with it. Only for a row reached from the answer's own — one read inside a subquery
        // is no row of the result — and never where the rows are in memory, where reading a key
        // through a navigation that is not there would fault rather than answer null.
        if (use is ScryDisclosureFieldUse.Returned or ScryDisclosureFieldUse.Capability &&
            origin is {Rooted: true, Via.Length: > 0})
        {
            Reach(origin);
        }
    }

    /// <summary>Records a member read directly, with no path: a leaf of the default projection.</summary>
    public void Leaf(ParameterExpression row, TypeMeta meta, Member member)
    {
        var origin = Start(row);
        var use = ScryDisclosureFieldUse.Returned;
        if (member.Kind == MemberKind.Capability)
        {
            use = ScryDisclosureFieldUse.Capability;
        }

        Record(origin, origin.Prefix + member.Name, use, member.Sensitive || meta.Sensitive);
    }

    /// <summary>Begins noting the members read from here on, for the group key they make up.</summary>
    public void BeginKey() =>
        capturing = [];

    /// <summary>The members read since <see cref="BeginKey"/>.</summary>
    public IReadOnlyList<ScryDisclosureField> EndKey()
    {
        var captured = capturing ?? [];
        capturing = null;
        return captured;
    }

    /// <summary>Remembers what each of the query's group keys read, in the order they were grouped by.</summary>
    public void Grouped(IReadOnlyList<IReadOnlyList<ScryDisclosureField>> read) =>
        groupKeys = read;

    /// <summary>
    /// A group key was read off the grouping. Where that is to return it, the members the key was made
    /// from are returned: they were only read when the rows were grouped, and are never read again.
    /// </summary>
    public void GroupKey(int index)
    {
        if (Suspended ||
            Use != ScryDisclosureFieldUse.Returned ||
            index >= groupKeys.Count)
        {
            return;
        }

        foreach (var field in groupKeys[index])
        {
            if (field.Use == ScryDisclosureFieldUse.Read)
            {
                fields.Add(
                    field with
                    {
                        Use = ScryDisclosureFieldUse.Returned
                    });
            }
        }
    }

    void Reach(Origin origin)
    {
        if (inMemory ||
            reached.Any(_ => _.Via == origin.Via) ||
            KeyOf(origin.Row.Type) is not { } key)
        {
            return;
        }

        // Able to carry a null: a navigation that leads nowhere, or to a row a policy hides, reads
        // as no key, and no key is recorded as no row.
        reached.Add((SourceName(origin.Row.Type), origin.Via, [.. key.Select(_ => Widened(Expression.Property(origin.Row, _)))]));
    }

    /// <summary>
    /// Begins a projection whose rows are rows of the answer: forgets what the last one reached, and
    /// names the answer's own row where it has a key to name it by.
    /// </summary>
    /// <param name="row">The row a projected row is read from.</param>
    /// <param name="via">How it is named: empty for the row itself, <c>inner</c> for a join's other side.</param>
    /// <param name="optional">Whether the row may be absent, as the unmatched side of an outer join is.</param>
    public void Begin(ParameterExpression row, string via = "", bool optional = false)
    {
        if (via.Length == 0)
        {
            reached.Clear();
        }

        Row(row, via);
        if (KeyOf(row.Type) is not { } key)
        {
            return;
        }

        // An absent side is a null row in memory, and a key cannot be read off one.
        if (optional && inMemory)
        {
            return;
        }

        reached.Add(
            (
                SourceName(row.Type),
                via,
                [
                    .. key.Select(
                        _ =>
                        {
                            Expression value = Expression.Property(row, _);
                            if (optional)
                            {
                                return Widened(value);
                            }

                            return value;
                        })
                ]));
    }

    /// <summary>
    /// Ends a projection: appends the key of every row it reached to the selector's leaves, after
    /// everything that is written, and answers where each lies. Null where it reached none.
    /// </summary>
    public IReadOnlyList<DisclosureSlot>? End(List<Expression> leaves)
    {
        if (reached.Count == 0)
        {
            return null;
        }

        var slots = new List<DisclosureSlot>(reached.Count);
        foreach (var (source, via, key) in reached)
        {
            slots.Add(new(source, via, leaves.Count, key.Count));
            leaves.AddRange(key);
        }

        reached.Clear();
        return slots;
    }

    static Expression Widened(Expression expression)
    {
        if (expression.Type.IsValueType &&
            Nullable.GetUnderlyingType(expression.Type) is null)
        {
            return Expression.Convert(expression, QueryComposition.Close(typeof(Nullable<>), expression.Type));
        }

        return expression;
    }

    /// <summary>
    /// The members that make up a type's key, in the key's own order, or null where it has none the
    /// audit can read: a type that is not an entity, one mapped with no key, one whose key is held in
    /// shadow. A key the host declared for a source that has none stands in for it.
    /// </summary>
    public IReadOnlyList<PropertyInfo>? KeyOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (keys.TryGetValue(type, out var known))
        {
            return known;
        }

        return keys[type] = Key(model, settings, type);
    }

    /// <summary>The same, for a caller with no request in hand: the startup check, a command's target.</summary>
    public static IReadOnlyList<PropertyInfo>? Key(IModel model, ScryDisclosureOptions settings, Type type)
    {
        for (var declared = type; declared is not null; declared = declared.BaseType)
        {
            if (settings.Keys.TryGetValue(declared, out var stated))
            {
                return stated;
            }
        }

        if (model.FindEntityType(type)?.FindPrimaryKey() is not { } primary)
        {
            return null;
        }

        var members = new List<PropertyInfo>(primary.Properties.Count);
        foreach (var property in primary.Properties)
        {
            // Held by EF with no member to read it through. The row has a key and the audit cannot
            // name it, which is no key as far as a record goes.
            if (property.PropertyInfo is not { } member)
            {
                return null;
            }

            members.Add(member);
        }

        return members;
    }

    /// <summary>
    /// A row's key in the order the record keeps keys in — the key's own — from values that arrived
    /// in the order the wire keeps them in, which is by member name. Null where the row has no key
    /// the record can name, or the two do not name the same members.
    /// </summary>
    /// <remarks>
    /// An attachment and a command name their row by key on the wire, ordered by name because that is
    /// an order a client and a server reach from metadata alone. A query records the same row in the
    /// model's order. Left as they arrive, a row of two key values would be recorded as one row by a
    /// query and as another by a fetch of the same row.
    /// </remarks>
    public static string? OrderedKey(
        IModel model,
        ScryDisclosureOptions audit,
        Type type,
        IReadOnlyList<Member> members,
        IReadOnlyList<object?> values)
    {
        if (Key(model, audit, type) is not { } key ||
            key.Count != members.Count)
        {
            return null;
        }

        var ordered = new object?[key.Count];
        for (var index = 0; index < key.Count; index++)
        {
            var found = -1;
            for (var candidate = 0; candidate < members.Count; candidate++)
            {
                if (members[candidate].Property?.Name == key[index].Name)
                {
                    found = candidate;
                    break;
                }
            }

            if (found < 0 ||
                values[found] is null)
            {
                return null;
            }

            ordered[index] = values[found];
        }

        return ScryDisclosureEntity.KeyOf(ordered);
    }

    /// <summary>
    /// What a row of a type is recorded under: the wire name of the highest type of its hierarchy
    /// that is a source. So a row read as a <c>Vehicle</c> and asked about as an <c>Asset</c> is one
    /// row, as it is in the database.
    /// </summary>
    public string SourceName(Type type) =>
        SourceName(schema, type);

    public static string SourceName(Schema schema, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        string? top = null;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (schema.TryGetSourceForType(current, out var source))
            {
                top = source.Name;
            }
        }

        return top ?? schema.WireName(type);
    }
}

/// <summary>
/// Where one of a projected row's keys lies: the row it identifies, and the slots past the written
/// ones that hold its key's values.
/// </summary>
/// <param name="Source">The source the row belongs to.</param>
/// <param name="Via">How the answer's row reaches it: empty for the row itself.</param>
/// <param name="First">The first slot of the key.</param>
/// <param name="Count">How many slots the key takes.</param>
readonly record struct DisclosureSlot(string Source, string Via, int First, int Count);
