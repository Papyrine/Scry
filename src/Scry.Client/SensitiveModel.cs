/// <summary>
/// Answers <see cref="SensitiveWalk"/>'s question from the generated models: is this member path, off
/// this source, one the server's model marked <c>[Sensitive]</c>?
/// </summary>
/// <remarks>
/// <para>
/// Everything it knows comes from the <c>[ScrySensitive]</c> attributes the generator emitted, so a
/// hand-built source — one opened with <c>Source&lt;T&gt;</c> against a type nothing generated — has
/// nothing to read and reports nothing. That is not a hole in the guarantee: the server re-derives
/// sensitivity from its own model and refuses a URL that broke the rule whatever the client believed.
/// </para>
/// <para>
/// Where a path cannot be resolved — the walk lost the row after a flatten or a group, or a segment
/// names something this client cannot see — the answer is whether <b>any</b> model marks a member of
/// that name. Deliberately blunt, and deliberately the same bluntness the server applies: erring
/// toward a body costs a client the cache, where the two sides disagreeing would cost it the query.
/// </para>
/// </remarks>
static class SensitiveModel
{
    static readonly ConcurrentDictionary<Type, Model> models = new();

    static readonly ConcurrentDictionary<string, Type> bySource = new(StringComparer.Ordinal);

    static readonly HashSet<string> anyName = new(StringComparer.Ordinal);

    static readonly Lock gate = new();

    sealed record Model(bool Sensitive, Dictionary<string, PropertyInfo> Members, HashSet<string> SensitiveMembers);

    /// <summary>
    /// Registers the query model a source is read as, so a path naming that source can be resolved
    /// later from the wire request alone — where the CLR type is long gone.
    /// </summary>
    public static void Register(string source, Type model)
    {
        bySource[source] = model;
        Describe(model);
    }

    /// <summary>The resolver <see cref="SensitiveWalk.Inspect"/> is driven with.</summary>
    public static bool IsSensitive(string? source, IReadOnlyList<string> path)
    {
        if (source is null ||
            !bySource.TryGetValue(source, out var model))
        {
            return Unresolved(path);
        }

        // An empty path asks about the source itself: what a query with no Select returns.
        var described = Describe(model);
        if (path.Count == 0)
        {
            return described.Sensitive || described.SensitiveMembers.Count > 0;
        }

        for (var i = 0; i < path.Count; i++)
        {
            if (described.Sensitive)
            {
                return true;
            }

            if (!described.Members.TryGetValue(path[i], out var property))
            {
                // A segment this client cannot see. It may be a member of a shape the walk could not
                // name; answer as though the path were unresolved rather than as though it were safe.
                return Unresolved(path);
            }

            if (described.SensitiveMembers.Contains(path[i]))
            {
                return true;
            }

            described = Describe(Unwrap(property.PropertyType));
        }

        return described.Sensitive;
    }

    static bool Unresolved(IReadOnlyList<string> path)
    {
        lock (gate)
        {
            return path.Any(anyName.Contains);
        }
    }

    /// <summary>
    /// The query model a source name was registered with, for tooling that reads a request back
    /// against the model it was written from — the renderer resolving an <c>OfType</c> target or an
    /// enum constant's type. Null for a source this process never opened.
    /// </summary>
    public static Type? ModelFor(string source) =>
        bySource.GetValueOrDefault(source);

    /// <summary>The property a member name resolves to on a model, from the same cached description
    /// the sensitivity walk reads. Null where the model does not declare it.</summary>
    public static PropertyInfo? Property(Type model, string name) =>
        Describe(model).Members.GetValueOrDefault(name);

    /// <summary>The type a path walk steps into after a member: the member's own type, unwrapped of
    /// nullability, or a collection's element.</summary>
    public static Type Element(Type type) =>
        Unwrap(type);

    static Model Describe(Type model) =>
        models.GetOrAdd(
            model,
            type =>
            {
                var members = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
                var sensitive = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    members[property.Name] = property;
                    if (property.IsDefined(typeof(ScrySensitiveAttribute), inherit: false))
                    {
                        sensitive.Add(property.Name);
                    }
                }

                if (sensitive.Count > 0)
                {
                    lock (gate)
                    {
                        anyName.UnionWith(sensitive);
                    }
                }

                return new(type.IsDefined(typeof(ScrySensitiveAttribute), inherit: false), members, sensitive);
            });

    // A navigation is nullable and a collection stands for its element; neither changes which members
    // the next segment can name.
    static Type Unwrap(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        if (unwrapped.IsGenericType &&
            unwrapped.GetInterfaces().Append(unwrapped).FirstOrDefault(_ => _.IsGenericType && _.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } enumerable)
        {
            return enumerable.GetGenericArguments()[0];
        }

        return unwrapped;
    }
}
