using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Skry;

/// <summary>How a member of a queryable type may be used.</summary>
enum MemberKind
{
    /// <summary>A scalar/enum/string value — usable in predicates, ordering and projection leaves.</summary>
    Scalar,

    /// <summary>A reference navigation to another queryable type — traversable in a member path.</summary>
    Navigation
}

/// <summary>An allow-listed member of a queryable type.</summary>
sealed class SkryMember(string name, PropertyInfo property, MemberKind kind)
{
    public string Name { get; } = name;
    public PropertyInfo Property { get; } = property;
    public Type Type { get; } = property.PropertyType;
    public MemberKind Kind { get; } = kind;
}

/// <summary>The allow-listed surface of a queryable CLR type.</summary>
sealed class SkryTypeMeta(Type clrType)
{
    public Type ClrType { get; } = clrType;
    public Dictionary<string, SkryMember> Members { get; } = new(StringComparer.Ordinal);
}

/// <summary>A registered queryable source (entity, view, or POCO).</summary>
sealed class SkrySource(
    string name,
    Type clrType,
    SourceKind kind,
    Type? policyType,
    Func<DbContext, IServiceProvider, IQueryable> resolve)
{
    public string Name { get; } = name;
    public Type ClrType { get; } = clrType;
    public SourceKind Kind { get; } = kind;
    public Type? PolicyType { get; } = policyType;
    public Func<DbContext, IServiceProvider, IQueryable> Resolve { get; } = resolve;
}

/// <summary>
/// The server's authoritative allow-list, built once from the model assembly's annotations. The
/// generator and the server derive the same surface from the same attributes; this is the runtime
/// source of truth that every incoming query is validated against.
/// </summary>
sealed class SkrySchema
{
    readonly Dictionary<string, SkrySource> sources = new(StringComparer.Ordinal);
    readonly Dictionary<Type, SkryTypeMeta> types = [];

    public bool TryGetSource(string name, out SkrySource source) =>
        sources.TryGetValue(name, out source!);

    public bool TryGetType(Type type, out SkryTypeMeta meta) =>
        types.TryGetValue(type, out meta!);

    public static SkrySchema Build(SkryOptions options)
    {
        if (options.ContextType is not { } contextType)
        {
            throw new InvalidOperationException(
                "No model configured. Call options.UseModel<TContext>() in AddSkry.");
        }

        var schema = new SkrySchema();
        var discovered = new List<(Type Type, string Name, SourceKind Kind, Type? Policy)>();

        foreach (var type in contextType.Assembly.GetTypes())
        {
            if (TryClassify(type, out var kind, out var name))
            {
                var policy = ResolvePolicy(type, options);
                discovered.Add((type, name, kind, policy));
            }
        }

        var queryableTypes = discovered.Select(_ => _.Type).ToHashSet();

        // Pass 1: build the allow-listed member metadata for every queryable type.
        foreach (var type in queryableTypes)
        {
            schema.types[type] = BuildTypeMeta(type, queryableTypes);
        }

        // Pass 2: register each source with its resolver.
        foreach (var (type, name, kind, policy) in discovered)
        {
            if (schema.sources.ContainsKey(name))
            {
                throw new InvalidOperationException($"Duplicate queryable source name '{name}'.");
            }

            schema.sources[name] = new(name, type, kind, policy, BuildResolver(type, kind, options));
        }

        return schema;
    }

    static bool TryClassify(Type type, out SourceKind kind, out string name)
    {
        // v1: the source name is the type name, matching the generator (which reads metadata only).
        kind = default;
        name = type.Name;

        if (type.GetCustomAttribute<QueryablePocoAttribute>() is not null)
        {
            kind = SourceKind.Poco;
            return true;
        }

        if (type.GetCustomAttribute<QueryableViewAttribute>() is not null)
        {
            kind = SourceKind.View;
            return true;
        }

        if (type.GetCustomAttribute<QueryableAttribute>() is not null)
        {
            // EF [Keyless] on a [Queryable] type means it is a view.
            kind = IsKeyless(type) ? SourceKind.View : SourceKind.Entity;
            return true;
        }

        return false;
    }

    static bool IsKeyless(Type type) =>
        type.GetCustomAttributes()
            .Any(_ => _.GetType().FullName == "Microsoft.EntityFrameworkCore.KeylessAttribute");

    static Type? ResolvePolicy(Type type, SkryOptions options)
    {
        if (options.Policies.TryGetValue(type, out var configured))
        {
            return configured;
        }

        return type.GetCustomAttribute<ReturnableWithAttribute>()?.PolicyType;
    }

    static SkryTypeMeta BuildTypeMeta(Type type, HashSet<Type> queryableTypes)
    {
        var meta = new SkryTypeMeta(type);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is not { IsPublic: true } ||
                property.GetIndexParameters().Length > 0 ||
                property.GetCustomAttribute<QueryIgnoreAttribute>() is not null)
            {
                continue;
            }

            if (IsScalar(property.PropertyType))
            {
                meta.Members[property.Name] = new(property.Name, property, MemberKind.Scalar);
            }
            else if (queryableTypes.Contains(property.PropertyType))
            {
                // A reference navigation to another allow-listed type is traversable.
                meta.Members[property.Name] = new(property.Name, property, MemberKind.Navigation);
            }

            // Anything else (collections, non-queryable complex types) is intentionally excluded.
        }

        return meta;
    }

    static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum || underlying.IsPrimitive)
        {
            return true;
        }

        return underlying == typeof(string) ||
               underlying == typeof(decimal) ||
               underlying == typeof(DateTime) ||
               underlying == typeof(DateOnly) ||
               underlying == typeof(TimeOnly) ||
               underlying == typeof(DateTimeOffset) ||
               underlying == typeof(TimeSpan) ||
               underlying == typeof(Guid);
    }

    static readonly MethodInfo setMethod = typeof(DbContext)
        .GetMethods()
        .Single(_ => _ is { Name: "Set", IsGenericMethod: true } && _.GetParameters().Length == 0);

    static Func<DbContext, IServiceProvider, IQueryable> BuildResolver(
        Type type,
        SourceKind kind,
        SkryOptions options)
    {
        if (kind == SourceKind.Poco)
        {
            if (!options.PocoSources.TryGetValue(type, out var factory))
            {
                throw new InvalidOperationException(
                    $"POCO source '{type.Name}' has no data registered. Call options.AddPocoSource<{type.Name}>(...).");
            }

            return (_, services) => factory(services);
        }

        var typedSet = setMethod.MakeGenericMethod(type);
        return (db, _) => (IQueryable)typedSet.Invoke(db, null)!;
    }
}
