using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// The server's authoritative allow-list, built once from the model assembly's annotations. The
/// generator and the server derive the same surface from the same attributes; this is the runtime
/// source of truth that every incoming query is validated against.
/// </summary>
sealed class Schema
{
    readonly Dictionary<string, ScrySource> sources = new(StringComparer.Ordinal);
    readonly Dictionary<Type, TypeMeta> types = [];

    // Captured for the startup guardrail (ValidateAgainstModel): the CLR types the annotations claim
    // are EF-mapped entities/views versus the ones claimed to be complex value types. The classifiers
    // work from attributes alone; only the live EF model can confirm the claim is right.
    readonly List<Type> entitySourceTypes = [];
    readonly List<Type> complexTypes = [];

    public bool TryGetSource(string name, [MaybeNullWhen(false)] out ScrySource source) =>
        sources.TryGetValue(name, out source);

    public bool TryGetType(Type type, [MaybeNullWhen(false)] out TypeMeta meta) =>
        types.TryGetValue(type, out meta);

    /// <summary>
    /// Projects the allow-list into the public introspection contract. Type displays mirror the
    /// source generator's emission exactly, so a client can synthesize byte-compatible query models.
    /// Ordered for deterministic output.
    /// </summary>
    public ScryIntrospection Describe(ScryOptions options)
    {
        var enums = new Dictionary<string, ScryEnumInfo>(StringComparer.Ordinal);

        var typeInfos = types.Values
            .OrderBy(_ => _.ClrType.Name, StringComparer.Ordinal)
            .Select(_ => new ScryTypeInfo(
                $"{_.ClrType.Name}QueryModel",
                _.Members.Values
                    .OrderBy(_ => _.Name, StringComparer.Ordinal)
                    .Select(_ => DescribeMember(_, enums))
                    .ToList()))
            .ToList();

        var sourceInfos = sources.Values
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .Select(_ => new ScrySourceInfo(_.Name, _.Kind.ToString(), $"{_.ClrType.Name}QueryModel"))
            .ToList();

        var enumInfos = enums.Values
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .ToList();

        return new(ScryIntrospection.CurrentVersion, options.MaxPageSize, sourceInfos, typeInfos, enumInfos);
    }

    static ScryMemberInfo DescribeMember(Member member, Dictionary<string, ScryEnumInfo> enums)
    {
        if (member.Kind == MemberKind.Navigation)
        {
            // Unwrap Nullable<T> so an optional struct complex member displays as its model, not Nullable`1.
            var target = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
            return new(member.Name, $"{target.Name}QueryModel?", NeedsNullDefault: false, IsNavigation: true);
        }

        var underlying = Nullable.GetUnderlyingType(member.Type);
        var nullable = underlying is not null;
        var actual = underlying ?? member.Type;

        if (actual.IsEnum)
        {
            enums.TryAdd(actual.Name, new(actual.Name, Enum.GetNames(actual)));

            return new(member.Name, nullable ? $"{actual.Name}?" : actual.Name, NeedsNullDefault: false, IsNavigation: false);
        }

        var display = ScalarDisplay(actual);
        if (display is "string" or "byte[]")
        {
            // A non-nullable reference-type scalar needs ' = null!;' to satisfy nullable analysis,
            // matching the generator.
            return new(member.Name, display, NeedsNullDefault: true, IsNavigation: false);
        }

        return new(member.Name, nullable ? $"{display}?" : display, NeedsNullDefault: false, IsNavigation: false);
    }

    // Mirrors MetadataModelReader's PrimitiveKeyword + ScalarKeyword so introspection type displays
    // are identical to generated code.
    static string ScalarDisplay(Type type) =>
        type.FullName switch
        {
            "System.Boolean" => "bool",
            "System.Char" => "char",
            "System.SByte" => "sbyte",
            "System.Byte" => "byte",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.String" => "string",
            "System.Decimal" => "decimal",
            "System.DateTime" => "global::System.DateTime",
            "System.DateOnly" => "global::System.DateOnly",
            "System.TimeOnly" => "global::System.TimeOnly",
            "System.DateTimeOffset" => "global::System.DateTimeOffset",
            "System.TimeSpan" => "global::System.TimeSpan",
            "System.Guid" => "global::System.Guid",
            // byte[] has no ScalarKeyword counterpart; the metadata side reaches it via SignatureDecoder's
            // GetSZArrayType -> BytesDecoded, since arrays are not NamedDecoded.
            "System.Byte[]" => "byte[]",
            _ => type.Name
        };

    public static Schema Build(ScryOptions options)
    {
        if (options.ContextType is not { } contextType)
        {
            throw new("No model configured. Call options.UseModel<TContext>() in AddScry.");
        }

        var schema = new Schema();
        var discovered = new List<(Type Type, string Name, SourceKind Kind, Type? Policy)>();

        foreach (var type in contextType.Assembly.GetTypes())
        {
            if (TryClassify(type, out var kind, out var name))
            {
                var policy = ResolvePolicy(type, options);
                discovered.Add((type, name, kind, policy));
                if (kind is SourceKind.Entity or SourceKind.View)
                {
                    schema.entitySourceTypes.Add(type);
                }
            }
            else if (type.GetCustomAttribute<QueryableComplexAttribute>() is not null)
            {
                // A complex type is a traversable member type, not a root source: it gets member
                // metadata (below) but no source entry and no resolver.
                schema.complexTypes.Add(type);
            }
        }

        // Navigation targets are every opted-in type — the sources plus the complex value types.
        var queryableTypes = discovered.Select(_ => _.Type).Concat(schema.complexTypes).ToHashSet();

        // Pass 1: build the allow-listed member metadata for every queryable type.
        foreach (var type in queryableTypes)
        {
            schema.types[type] = BuildTypeMeta(type, queryableTypes);
        }

        // Pass 2: register each source with its resolver. Complex types are deliberately absent.
        foreach (var (type, name, kind, policy) in discovered)
        {
            if (schema.sources.ContainsKey(name))
            {
                throw new($"Duplicate queryable source name '{name}'.");
            }

            schema.sources[name] = new(name, type, kind, policy, BuildResolver(type, kind, options));
        }

        return schema;
    }

    /// <summary>
    /// Confirms the annotations match the live EF model, throwing a directed error otherwise. The
    /// generator and the reflection classifier both work from attributes alone (the generator never
    /// sees <c>OnModelCreating</c>), so a type marked <c>[Queryable]</c> that is really a complex type,
    /// or a <c>[QueryableComplex]</c> that is really an entity, would otherwise fail obscurely at query
    /// time. Invoked once at startup from <c>MapScry</c>, where a live model is available.
    /// </summary>
    public void ValidateAgainstModel(IModel model, Type contextType)
    {
        // The CLR types EF actually maps as complex types. A source-annotated type appearing here (or a
        // complex-annotated type that is really a mapped entity) is the mix-up worth catching; a type
        // simply absent from the model is left alone, since [Queryable] is deliberately allowed on types
        // that carry no DbSet.
        var complexClrTypes = model.GetEntityTypes()
            .SelectMany(_ => _.GetComplexProperties())
            .Select(_ => _.ComplexType.ClrType)
            .ToHashSet();

        foreach (var type in entitySourceTypes)
        {
            if (complexClrTypes.Contains(type))
            {
                throw new(
                    $"'{type.Name}' is marked [Queryable]/[QueryableView] but is an EF complex type in " +
                    $"{contextType.Name}. Use [QueryableComplex].");
            }
        }

        foreach (var type in complexTypes)
        {
            if (model.FindEntityType(type) is not null)
            {
                throw new(
                    $"'{type.Name}' is marked [QueryableComplex] but is a mapped entity in " +
                    $"{contextType.Name}. Use [Queryable] (or [QueryableView] for a keyless view).");
            }
        }
    }

    static bool TryClassify(Type type, out SourceKind kind, out string name)
    {
        // The source name defaults to the type name and is overridden by the attribute's Name. This
        // must stay in lockstep with the generator's MetadataModelReader.TryClassify, which derives
        // the same name from metadata alone.
        kind = default;
        name = type.Name;

        if (type.GetCustomAttribute<QueryablePocoAttribute>() is { } poco)
        {
            kind = SourceKind.Poco;
            name = Named(poco.Name, name);
            return true;
        }

        if (type.GetCustomAttribute<QueryableViewAttribute>() is { } view)
        {
            kind = SourceKind.View;
            name = Named(view.Name, name);
            return true;
        }

        if (type.GetCustomAttribute<QueryableAttribute>() is { } queryable)
        {
            // EF [Keyless] on a [Queryable] type means it is a view.
            kind = IsKeyless(type) ? SourceKind.View : SourceKind.Entity;
            name = Named(queryable.Name, name);
            return true;
        }

        return false;
    }

    // A blank Name is treated as unset, matching the generator.
    static string Named(string? configured, string fallback)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallback;
        }

        return configured;
    }

    static bool IsKeyless(Type type) =>
        type.GetCustomAttribute<KeylessAttribute>() is not null;

    static Type? ResolvePolicy(Type type, ScryOptions options)
    {
        if (options.Policies.TryGetValue(type, out var configured))
        {
            return configured;
        }

        return type.GetCustomAttribute<ReturnableWithAttribute>()?.Policy;
    }

    static TypeMeta BuildTypeMeta(Type type, HashSet<Type> queryableTypes)
    {
        var meta = new TypeMeta(type);

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
                continue;
            }

            // A reference navigation or a complex value type: traversable when the target is opted in.
            // Unwrap Nullable<T> so an optional struct complex member (Address?) resolves to Address.
            var target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (queryableTypes.Contains(target))
            {
                meta.Members[property.Name] = new(property.Name, property, MemberKind.Navigation);
            }

            // Anything else (collections, non-queryable complex types) is intentionally excluded.
        }

        return meta;
    }

    static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum ||
            underlying.IsPrimitive)
        {
            return true;
        }

        return underlying == typeof(string) ||
               underlying == typeof(decimal) ||
               underlying == typeof(DateTime) ||
               underlying == typeof(Date) ||
               underlying == typeof(Time) ||
               underlying == typeof(DateTimeOffset) ||
               underlying == typeof(TimeSpan) ||
               underlying == typeof(Guid) ||
               underlying == typeof(byte[]);
    }

    static readonly MethodInfo setMethod = typeof(DbContext)
        .GetMethods()
        .Single(_ => _ is { Name: "Set", IsGenericMethod: true } &&
                     _.GetParameters().Length == 0);

    static Func<DbContext, IServiceProvider, IQueryable> BuildResolver(
        Type type,
        SourceKind kind,
        ScryOptions options)
    {
        if (kind == SourceKind.Poco)
        {
            if (options.PocoSources.TryGetValue(type, out var factory))
            {
                return (_, services) => factory(services);
            }

            throw new($"POCO source '{type.Name}' has no data registered. Call options.AddPocoSource<{type.Name}>(...).");
        }

        var typedSet = setMethod.MakeGenericMethod(type);
        return (db, _) => (IQueryable) typedSet.Invoke(db, null)!;
    }
}
