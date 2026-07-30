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

    // Previous wire names still answered to, kept apart from the current surface above so they never
    // leak into introspection or the stamp. Enum values are keyed by enum type, then previous name.
    readonly Dictionary<string, ScrySource> sourcePreviousNames = new(StringComparer.Ordinal);
    readonly Dictionary<Type, Dictionary<string, string>> enumPreviousNames = [];

    // Captured for the startup guardrail (ValidateAgainstModel): the CLR types the annotations claim
    // are EF-mapped entities/views versus the ones claimed to be complex value types. The classifiers
    // work from attributes alone; only the live EF model can confirm the claim is right.
    readonly List<Type> entitySourceTypes = [];
    readonly List<Type> complexTypes = [];

    public bool TryGetSource(string name, [MaybeNullWhen(false)] out ScrySource source) =>
        sources.TryGetValue(name, out source) ||
        sourcePreviousNames.TryGetValue(name, out source);

    public bool TryGetType(Type type, [MaybeNullWhen(false)] out TypeMeta meta) =>
        types.TryGetValue(type, out meta);

    /// <summary>
    /// Maps an enum value name off the wire to its current name, translating one a client was
    /// generated against before the value was renamed. Unknown names are returned unchanged, so the
    /// caller reports them against the real enum.
    /// </summary>
    public string ResolveEnumValue(Type enumType, string name)
    {
        if (enumPreviousNames.TryGetValue(enumType, out var previous) &&
            previous.TryGetValue(name, out var current))
        {
            return current;
        }

        return name;
    }

    /// <summary>
    /// The enum renames declared by [PreviousNames], in wire form: current value name to the previous
    /// names still honoured. Attached to responses for a drifted client, whose reader resolves a value
    /// name it does not know to one it does — the response-side counterpart of
    /// <see cref="ResolveEnumValue"/>. Empty when no exposed enum value carries a previous name.
    /// </summary>
    public IReadOnlyList<EnumAlias> EnumAliases { get; private set; } = [];

    IReadOnlyList<EnumAlias> BuildEnumAliases() =>
    [
        ..enumPreviousNames
            .SelectMany(entry => entry.Value
                .GroupBy(_ => _.Value)
                .Select(group => new EnumAlias(entry.Key.Name, group.Key, [..group.Select(_ => _.Key).Order(StringComparer.Ordinal)])))
            .OrderBy(_ => _.EnumName, StringComparer.Ordinal)
            .ThenBy(_ => _.ValueName, StringComparer.Ordinal)
    ];

    /// <summary>
    /// Projects the allow-list into the public introspection contract. Type displays mirror the
    /// source generator's emission exactly, so a client can synthesize byte-compatible query models.
    /// Ordered for deterministic output.
    /// </summary>
    public ScryIntrospection Describe(ScryOptions options)
    {
        var (sourceInfos, typeInfos, enumInfos) = DescribeSurface();
        return new(ScryIntrospection.CurrentVersion, options.MaxPageSize, sourceInfos, typeInfos, enumInfos)
        {
            SchemaStamp = Stamp
        };
    }

    /// <summary>
    /// A hash of the allow-listed surface, compared against <see cref="QueryRequest.Stamp"/> to
    /// distinguish a stale client from an invalid query. The generator computes the same value over
    /// the same canonical form (the shared SchemaStamp source), so client and server stamps agree
    /// exactly when their queryable surfaces do. Computed once, at build.
    /// </summary>
    public string Stamp { get; private set; } = "";

    string ComputeStamp()
    {
        var (sourceInfos, typeInfos, enumInfos) = DescribeSurface();
        return SchemaStamp.Compute(
            sourceInfos.Select(_ => (_.Name, _.Kind, _.Model)).ToList(),
            typeInfos.Select(_ => (_.Model, _.Members.Select(member => (member.Name, member.TypeDisplay)).ToList())).ToList(),
            enumInfos.Select(_ => (_.Name, _.Values.ToList())).ToList());
    }

    (List<ScrySourceInfo> Sources, List<ScryTypeInfo> Types, List<ScryEnumInfo> Enums) DescribeSurface()
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

        return (sourceInfos, typeInfos, enumInfos);
    }

    static ScryMemberInfo DescribeMember(Member member, Dictionary<string, ScryEnumInfo> enums)
    {
        // Mirrors the generator's emission exactly: the schema stamp hashes this string, so any
        // divergence would read as model drift on every client.
        if (member.Kind == MemberKind.Collection)
        {
            var element = CollectionElement(member.Type)!;
            return new(
                member.Name,
                $"global::System.Collections.Generic.IReadOnlyList<{element.Name}QueryModel>",
                NeedsNullDefault: true,
                IsNavigation: false,
                IsCollection: true);
        }

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

        EnsureCollationIsAName(options.CaseSensitiveCollation, nameof(options.CaseSensitiveCollation));
        EnsureCollationIsAName(options.CaseInsensitiveCollation, nameof(options.CaseInsensitiveCollation));

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

                // Only its members are ever named on the wire, so a previous name on the type itself
                // has nothing to apply to.
                if (PreviousNamesOf(type).Count > 0)
                {
                    throw new($"[PreviousNames] on '{type.Name}' has no effect: a [QueryableComplex] type is not a source and has no wire name. Put it on the renamed member instead.");
                }
            }
            else if (PreviousNamesOf(type).Count > 0)
            {
                throw new($"[PreviousNames] on '{type.Name}', which has no wire name: it is not an opted-in source. Put it on a [Queryable]/[QueryableView]/[QueryablePoco] type, an exposed member, or an enum value.");
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

        // Pass 3: register the previous names sources still answer to. Deferred until every current
        // name is known, so a previous name can never shadow a live source whatever the discovery order.
        foreach (var (type, name, _, _) in discovered)
        {
            schema.RegisterSourcePreviousNames(type, schema.sources[name]);
        }

        // A row policy filters a source. A subquery over a collection has no source for it to filter,
        // so aggregating over a policied type would count exactly the rows the policy exists to hide.
        // Refusing here makes that a startup failure rather than a silent leak at query time.
        var policied = discovered
            .Where(_ => _.Policy is not null)
            .Select(_ => _.Type)
            .ToHashSet();
        foreach (var (owner, member) in schema.types.Values
                     .SelectMany(meta => meta.Members.Values.Select(member => (meta.ClrType, member)))
                     .Where(_ => _.member.Kind == MemberKind.Collection))
        {
            var element = CollectionElement(member.Type)!;
            if (policied.Contains(element))
            {
                throw new(
                    $"'{owner.Name}.{member.Name}' is a [QueryableCollection] of '{element.Name}', which has a row policy. " +
                    "Aggregating a collection cannot apply that policy — a policy filters a source, and a subquery has none — " +
                    "so exposing it would count rows the policy hides. Remove the attribute, or drop the policy.");
            }
        }

        // Enum value names travel on the wire as constants, so a renamed value needs the same
        // migration window as a source or a member. Only enums reachable from an allow-listed scalar
        // member are on the wire at all.
        foreach (var enumType in schema.types.Values
                     .SelectMany(_ => _.Members.Values)
                     .Where(_ => _.Kind == MemberKind.Scalar)
                     .Select(_ => Nullable.GetUnderlyingType(_.Type) ?? _.Type)
                     .Where(_ => _.IsEnum)
                     .Distinct())
        {
            var previous = BuildEnumPreviousNames(enumType);
            if (previous.Count > 0)
            {
                schema.enumPreviousNames[enumType] = previous;
            }
        }

        schema.EnumAliases = schema.BuildEnumAliases();
        schema.Stamp = schema.ComputeStamp();
        return schema;
    }

    static IReadOnlyList<string> PreviousNamesOf(MemberInfo member) =>
        member.GetCustomAttribute<PreviousNamesAttribute>()?.Names ?? [];

    void RegisterSourcePreviousNames(Type type, ScrySource source)
    {
        foreach (var previous in PreviousNamesOf(type))
        {
            EnsureNotBlank(previous, $"'{type.Name}'");

            if (previous == source.Name)
            {
                throw new($"[PreviousNames] on '{type.Name}' lists '{previous}', which is already its current source name. Remove it.");
            }

            if (sources.TryGetValue(previous, out var live))
            {
                throw new($"[PreviousNames] on '{type.Name}' lists '{previous}', which is the current source name of '{live.ClrType.Name}'.");
            }

            if (sourcePreviousNames.TryGetValue(previous, out var claimed))
            {
                throw new($"[PreviousNames] on '{type.Name}' lists '{previous}', which is already a previous name of source '{claimed.Name}'.");
            }

            sourcePreviousNames[previous] = source;
        }
    }

    static Dictionary<string, string> BuildEnumPreviousNames(Type enumType)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var current = Enum.GetNames(enumType).ToHashSet(StringComparer.Ordinal);

        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            foreach (var previous in PreviousNamesOf(field))
            {
                EnsureNotBlank(previous, $"'{enumType.Name}.{field.Name}'");

                if (current.Contains(previous))
                {
                    throw new($"[PreviousNames] on '{enumType.Name}.{field.Name}' lists '{previous}', which is a current value of that enum.");
                }

                if (map.TryGetValue(previous, out var claimed))
                {
                    throw new($"[PreviousNames] on '{enumType.Name}.{field.Name}' lists '{previous}', which is already a previous name of '{enumType.Name}.{claimed}'.");
                }

                map[previous] = field.Name;
            }
        }

        return map;
    }

    static void EnsureNotBlank(string previous, string what)
    {
        if (string.IsNullOrWhiteSpace(previous))
        {
            throw new($"[PreviousNames] on {what} contains a blank name.");
        }
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
                throw new($"'{type.Name}' is marked [Queryable]/[QueryableView] but is an EF complex type in {contextType.Name}. Use [QueryableComplex].");
            }
        }

        foreach (var type in complexTypes)
        {
            if (model.FindEntityType(type) is not null)
            {
                throw new($"'{type.Name}' is marked [QueryableComplex] but is a mapped entity in {contextType.Name}. Use [Queryable] (or [QueryableView] for a keyless view).");
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

    /// <summary>
    /// A collation is the one configured value that reaches the database as SQL text rather than as a
    /// parameter, so its shape is checked at startup rather than trusted. Every real collation name is
    /// a bare identifier; anything else is a configuration mistake at best.
    /// </summary>
    /// <remarks>
    /// A request can never carry one — the wire names a <see cref="StringMatch"/> and the string is
    /// looked up here — so this guards the remaining path: a deployment wiring the option up from
    /// somewhere it does not control. Providers do quote the name, but that is provider-overridable
    /// behaviour rather than a guarantee this library can make, so it is not relied on.
    /// </remarks>
    static void EnsureCollationIsAName(string? collation, string option)
    {
        if (collation is null)
        {
            return;
        }

        if (collation.Length == 0 ||
            collation.Length > 128 ||
            !collation.All(_ => char.IsAsciiLetterOrDigit(_) || _ == '_'))
        {
            throw new(
                $"ScryOptions.{option} must be a plain collation name — letters, digits and underscores only. " +
                "It is emitted into SQL rather than parameterized, so it has to be trusted configuration, " +
                "never a value taken from a request or from anywhere a caller can influence.");
        }
    }

    static Type? ResolvePolicy(Type type, ScryOptions options)
    {
        if (options.Policies.TryGetValue(type, out var configured))
        {
            return configured;
        }

        return type.GetCustomAttribute<ReturnableWithAttribute>()?.Policy;
    }

    /// <summary>
    /// The element type of a collection member, or null when the type is not a collection. A string is
    /// excluded deliberately — it is <c>IEnumerable&lt;char&gt;</c> and is always a scalar here.
    /// </summary>
    public static Type? CollectionElement(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IEnumerable<>) ||
             typeof(IEnumerable<>).MakeGenericType(type.GetGenericArguments()[0]).IsAssignableFrom(type)))
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .FirstOrDefault(_ => _.IsGenericType && _.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
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
                EnsureNoPreviousNames(type, property);
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
                continue;
            }

            // A collection navigation is exposed only when the member asks for it and its element type
            // is itself opted in — default-deny applies to the member, not just to the type.
            if (property.GetCustomAttribute<QueryableCollectionAttribute>() is not null &&
                CollectionElement(property.PropertyType) is { } element &&
                queryableTypes.Contains(element))
            {
                meta.Members[property.Name] = new(property.Name, property, MemberKind.Collection);
                continue;
            }

            // Anything else (an un-opted-in collection, a non-queryable complex type) stays excluded.
            EnsureNoPreviousNames(type, property);
        }

        // Registered once the whole current surface is known, so a previous name cannot shadow a live
        // member that happens to be declared later in the type.
        foreach (var member in meta.Members.Values)
        {
            RegisterMemberPreviousNames(meta, member);
        }

        return meta;
    }

    static void RegisterMemberPreviousNames(TypeMeta meta, Member member)
    {
        var owner = meta.ClrType.Name;
        foreach (var previous in PreviousNamesOf(member.Property))
        {
            EnsureNotBlank(previous, $"'{owner}.{member.Name}'");

            if (previous == member.Name)
            {
                throw new($"[PreviousNames] on '{owner}.{member.Name}' lists '{previous}', which is already its current name. Remove it.");
            }

            if (meta.Members.ContainsKey(previous))
            {
                throw new($"[PreviousNames] on '{owner}.{member.Name}' lists '{previous}', which is a current member of '{owner}'.");
            }

            if (meta.PreviousNames.TryGetValue(previous, out var claimed))
            {
                throw new($"[PreviousNames] on '{owner}.{member.Name}' lists '{previous}', which is already a previous name of '{owner}.{claimed.Name}'.");
            }

            meta.PreviousNames[previous] = member;
        }
    }

    static void EnsureNoPreviousNames(Type type, PropertyInfo property)
    {
        if (PreviousNamesOf(property).Count > 0)
        {
            throw new($"[PreviousNames] on '{type.Name}.{property.Name}', which is not exposed to clients. Remove it, or remove whatever excludes the member.");
        }
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
