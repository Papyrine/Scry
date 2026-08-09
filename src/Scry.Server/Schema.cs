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
        // Deprecation is deliberately not hashed, matching the generator: [Obsolete] leaves the
        // queryable surface exactly as it was, and hashing it would report every deployed client as
        // stale over what is only a note to whoever next rebuilds one.
        var (sourceInfos, typeInfos, enumInfos) = DescribeSurface();
        return SchemaStamp.Compute(
            sourceInfos.Select(_ => (_.Name, _.Kind, _.Model)).ToList(),
            typeInfos.Select(_ => (_.Model, _.Base, StampMembers(_))).ToList(),
            enumInfos.Select(_ => (_.Name, _.Values.ToList())).ToList());
    }

    /// <summary>
    /// The members a type contributes to the stamp: its own, plus — for one carrying an attachment —
    /// a synthetic member naming the key that attachment is fetched by. Mirrors
    /// <c>ScryGenerator.StampMembers</c>; <c>~</c> cannot begin an identifier, so the synthetic name
    /// can never collide with a real member's, and a type without an attachment hashes exactly as it
    /// did before attachments existed.
    /// </summary>
    static List<(string, string)> StampMembers(ScryTypeInfo type)
    {
        var members = type.Members
            .Select(_ => (_.Name, _.TypeDisplay))
            .ToList();
        if (type.Keys is { } keys)
        {
            members.Add(("~keys", string.Join(" ", keys)));
        }

        return members;
    }

    (List<ScrySourceInfo> Sources, List<ScryTypeInfo> Types, List<ScryEnumInfo> Enums) DescribeSurface()
    {
        var enums = new Dictionary<string, ScryEnumInfo>(StringComparer.Ordinal);

        var typeInfos = types.Values
            .OrderBy(_ => _.ClrType.Name, StringComparer.Ordinal)
            .Select(meta => new ScryTypeInfo(
                $"{meta.ClrType.Name}QueryModel",
                // Declared members only. Reflection reports the base's properties here too, but the
                // generated model inherits them, so describing them again would make the two sides
                // disagree about the surface and read as drift on every client.
                Declared(meta)
                    .OrderBy(_ => _.Name, StringComparer.Ordinal)
                    .Select(_ => DescribeMember(_, enums))
                    .ToList())
            {
                Base = meta.Base is { } clrBase ? $"{clrBase.Name}QueryModel" : null,
                Obsolete = ObsoleteOf(meta.ClrType),
                // Carried only where something fetches by it, which keeps every other type's
                // description — and so its stamp — exactly what it was.
                Keys = meta.AttachmentKeys?.Select(_ => _.Name).ToList()
            })
            .ToList();

        var sourceInfos = sources.Values
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .Select(_ => new ScrySourceInfo(_.Name, _.Kind.ToString(), $"{_.ClrType.Name}QueryModel")
            {
                Obsolete = ObsoleteOf(_.ClrType)
            })
            .ToList();

        var enumInfos = enums.Values
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .ToList();

        return (sourceInfos, typeInfos, enumInfos);
    }

    // The members a type contributes itself: everything it exposes, less whatever its allow-listed
    // base already exposes under the same name.
    IEnumerable<Member> Declared(TypeMeta meta)
    {
        if (meta.Base is not { } clrBase ||
            !types.TryGetValue(clrBase, out var baseMeta))
        {
            return meta.Members.Values;
        }

        return meta.Members.Values.Where(_ => !baseMeta.Members.ContainsKey(_.Name));
    }

    static ScryMemberInfo DescribeMember(Member member, Dictionary<string, ScryEnumInfo> enums) =>
        DescribeShape(member, enums) with
        {
            Obsolete = ObsoleteOf(member.Property)
        };

    static ScryMemberInfo DescribeShape(Member member, Dictionary<string, ScryEnumInfo> enums)
    {
        // Mirrors the generator's emission exactly: the schema stamp hashes this string, so any
        // divergence would read as model drift on every client.
        if (member.Kind == MemberKind.Collection)
        {
            var element = CollectionElement(member.Type)!;

            // A collection of values (an EF primitive collection) spells its element as the scalar
            // itself; one of rows spells it as that type's query model.
            var argument = IsScalar(element) ? ScalarShape(element, enums) : $"{element.Name}QueryModel";
            return new(
                member.Name,
                $"global::System.Collections.Generic.IReadOnlyList<{argument}>",
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

        // An attachment is emitted as the handle rather than as the byte[] it is declared as, which is
        // exactly why — unlike [BinaryTransfer] — it moves the schema stamp. Mirrors ScryGenerator.Display.
        if (member.Kind == MemberKind.Attachment)
        {
            return new(member.Name, "global::Scry.ScryAttachment", NeedsNullDefault: true, IsNavigation: false)
            {
                IsAttachment = true
            };
        }

        var shape = ScalarShape(member.Type, enums);

        // A non-nullable reference-type scalar needs ' = null!;' to satisfy nullable analysis, matching
        // the generator.
        return new(member.Name, shape, NeedsNullDefault: shape is "string" or "byte[]", IsNavigation: false);
    }

    /// <summary>
    /// How a scalar is spelled in generated code — as a member's own type, or as the element of a
    /// collection of values. Any enum it names is collected on the way past, since an enum is only
    /// re-emitted to clients when something exposed reaches it.
    /// </summary>
    static string ScalarShape(Type type, Dictionary<string, ScryEnumInfo> enums)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var nullable = underlying is not null;
        var actual = underlying ?? type;

        if (actual.IsEnum)
        {
            enums.TryAdd(actual.Name, new(actual.Name, Enum.GetNames(actual)));
            return nullable ? $"{actual.Name}?" : actual.Name;
        }

        var display = ScalarDisplay(actual);
        if (display is "string" or "byte[]")
        {
            return display;
        }

        return nullable ? $"{display}?" : display;
    }

    /// <summary>
    /// The deprecation an annotated member or type carries, in the form introspection publishes it:
    /// null when it is not <c>[Obsolete]</c>, otherwise the message, or empty when the attribute gave
    /// none. Only the message is read — the <c>error</c> flag is dropped, because an obsolete member
    /// is still one this server validates and executes, and a client build break would claim
    /// otherwise. <c>[QueryIgnore]</c> is the hard stop.
    /// </summary>
    /// <remarks>
    /// <c>inherit: false</c>, matching the metadata side (attributes there are declared-only) and the
    /// opt-in attributes above. Must stay in lockstep with MetadataModelReader.ObsoleteOf, which the
    /// generator reads the same attribute with.
    /// </remarks>
    static string? ObsoleteOf(MemberInfo member)
    {
        if (member.GetCustomAttribute<ObsoleteAttribute>(inherit: false) is not { } obsolete)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(obsolete.Message))
        {
            return "";
        }

        return obsolete.Message;
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
        var discovered = new List<(Type Type, string Name, SourceKind Kind, IReadOnlyList<Type> Policies)>();

        foreach (var type in contextType.Assembly.GetTypes())
        {
            if (TryClassify(type, out var kind, out var name))
            {
                EnsureNameIsIdentifier(type, name);
                var policies = ResolvePolicies(type, name, options);
                discovered.Add((type, name, kind, policies));
                if (kind is SourceKind.Entity or SourceKind.View)
                {
                    schema.entitySourceTypes.Add(type);
                }
            }
            else if (type.HasAttribute<QueryableComplexAttribute>(inherit: false))
            {
                // A complex type is a traversable member type, not a root source: it gets member
                // metadata (below) but no source entry and no resolver.
                schema.complexTypes.Add(type);

                // A policy filters a source, and this type has none — so one attached here would never
                // run, whether the type is reached by traversal or aggregated as a [QueryableCollection]
                // of it. The equivalent mistake on a collection of entities is refused below; that check
                // can never fire for a complex element, because a complex type is never in `discovered`.
                if (type.HasAttribute<ReturnableWithAttribute>(inherit: false) ||
                    options.Policies.ContainsKey(type))
                {
                    throw new($"'{type.Name}' is [QueryableComplex] and carries a row policy, which cannot apply: a policy filters a source, and a complex type is a member type with no source of its own. Filter on the type that owns it instead.");
                }

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

        // Pass 1b: link each type to its nearest allow-listed base, which is what OfType narrows along
        // and what lets the generated models inherit rather than repeat the base's members. A base that
        // was not opted in is skipped over, so leaving it out hides it without hiding its descendants.
        foreach (var meta in schema.types.Values)
        {
            for (var candidate = meta.ClrType.BaseType; candidate is not null; candidate = candidate.BaseType)
            {
                if (queryableTypes.Contains(candidate))
                {
                    meta.Base = candidate;
                    break;
                }
            }
        }

        // Pass 2: register each source with its resolver. Complex types are deliberately absent.
        foreach (var (type, name, kind, policies) in discovered)
        {
            if (schema.sources.ContainsKey(name))
            {
                throw new($"Duplicate queryable source name '{name}'.");
            }

            schema.sources[name] = new(name, type, kind, policies, BuildResolver(type, kind, options))
            {
                AttachmentPolicy = ResolveAttachmentPolicy(schema, type, name, kind, options)
            };
        }

        // Pass 2b: derive the key every attachment is fetched by. Deferred until the member metadata
        // and base links exist, since a key declared on a base is still the derived row's.
        foreach (var meta in schema.types.Values)
        {
            if (Attachments(meta).Any())
            {
                meta.AttachmentKeys = DeriveKeys(meta);
            }
        }

        // An attachment on a complex type has no row to be fetched from — nothing keys it, and it is
        // never a source. Caught here rather than in BuildTypeMeta, which cannot yet tell a complex
        // type from an entity.
        foreach (var complex in schema.complexTypes)
        {
            if (schema.types[complex].Members.Values.FirstOrDefault(_ => _.Kind == MemberKind.Attachment) is { } member)
            {
                throw new(
                    $"'{complex.Name}.{member.Name}' is an [Attachment] on a [QueryableComplex] type, which has no row of its own to fetch it from. Move it to the entity that owns the complex member.");
            }
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
            .Where(_ => _.Policies.Count > 0)
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
                    $"'{owner.Name}.{member.Name}' is a [QueryableCollection] of '{element.Name}', which has a row policy. Aggregating a collection cannot apply that policy — a policy filters a source, and a subquery has none — so exposing it would count rows the policy hides. Remove the attribute, or drop the policy.");
            }
        }

        // Enum value names travel on the wire as constants, so a renamed value needs the same
        // migration window as a source or a member. Only enums reachable from an allow-listed scalar
        // member are on the wire at all.
        foreach (var enumType in schema.types.Values
                     .SelectMany(_ => _.Members.Values)
                     // A collection of values reaches an enum through its element, which is sent on the
                     // wire as a constant exactly as a scalar member's is.
                     .Select(member => member.Kind switch
                     {
                         MemberKind.Scalar => member.Type,
                         MemberKind.Collection => CollectionElement(member.Type),
                         _ => null
                     })
                     .Where(_ => _ is not null)
                     .Select(_ => Nullable.GetUnderlyingType(_!) ?? _!)
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

    /// <summary>
    /// Refuses a source name that cannot be written as a C# member name. A source name is not only a
    /// wire name — the generated client exposes it as a property, and so does the model the explorer
    /// synthesizes from introspection — so one that is not an identifier produces code neither can
    /// compile. The generator reports the same name as SCRY003; failing here means the mistake
    /// surfaces at startup whichever side is built first.
    /// </summary>
    /// <remarks>
    /// Only the current name is checked. A [PreviousNames] entry is a wire name and nothing else — the
    /// generator ignores it entirely — so it never has to be expressible in C#.
    /// </remarks>
    static void EnsureNameIsIdentifier(Type type, string name)
    {
        if (CSharpIdentifier.IsValid(name))
        {
            return;
        }

        throw new(
            $"Source name '{name}' on '{type.Name}' cannot be written as a C# property name. A source name is also the property the generated client and the explorer expose it as, so it has to be one C# can express. Set [Queryable(Name = \"...\")] to a plain identifier that is not a reserved keyword.");
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

        ValidateAttachmentKeys(model, contextType);
    }

    /// <summary>
    /// Confirms the key each attachment-bearing type was derived to have is the key EF really gives
    /// it. The derivation reads annotations and naming conventions alone, because the generator has to
    /// reach the same answer from metadata and never sees <c>OnModelCreating</c> — so a key configured
    /// fluently would leave the client fetching by one key and the server keyed on another. Comparing
    /// here turns that into a startup failure naming the fix.
    /// </summary>
    /// <remarks>
    /// Compared as a set: the derived order is ordinal by name and EF's is the declared one, and the
    /// wire order is the derived one on both sides, so only membership has to agree. A type absent
    /// from the model is skipped, matching how the checks above treat one.
    /// </remarks>
    void ValidateAttachmentKeys(IModel model, Type contextType)
    {
        foreach (var meta in types.Values)
        {
            if (meta.AttachmentKeys is not { } derived ||
                model.FindEntityType(meta.ClrType) is not { } entity)
            {
                continue;
            }

            var actual = entity.FindPrimaryKey()?.Properties.Select(_ => _.Name).ToList() ?? [];
            var expected = derived.Select(_ => _.Name).ToList();
            if (actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            {
                continue;
            }

            var attachment = Attachments(meta).First().Name;
            var describe = actual.Count == 0 ? "no primary key" : $"a primary key of ({string.Join(", ", actual)})";
            throw new(
                $"'{meta.ClrType.Name}.{attachment}' is an [Attachment], fetched by a key derived as ({string.Join(", ", expected)}), but {contextType.Name} gives '{meta.ClrType.Name}' {describe}. The derivation reads [Key] and the 'Id'/'{meta.ClrType.Name}Id' conventions, because a client is generated from the model's metadata and never sees OnModelCreating — so a fluently configured key cannot be found there. Mark the key member(s) with [Key] to state it where both sides can read it.");
        }
    }

    static bool TryClassify(Type type, out SourceKind kind, out string name)
    {
        // The source name defaults to the type name and is overridden by the attribute's Name. This
        // must stay in lockstep with the generator's MetadataModelReader.TryClassify, which derives
        // the same name from metadata alone.
        //
        // Every lookup here is inherit: false. An opt-in attribute is a statement about the type it is
        // written on, so a derived type has to opt in on its own — otherwise adding a subclass to the
        // model would silently expose it and its members, which is the opposite of default-deny. It is
        // also what the generator does, since metadata attributes are declared-only, and the two
        // classifiers have to agree. A row policy is the deliberate exception, and ResolvePolicies
        // walks the base chain for it: a subclass cannot shed the policy its base carries.
        kind = default;
        name = type.Name;

        if (type.GetCustomAttribute<QueryablePocoAttribute>(inherit: false) is { } poco)
        {
            kind = SourceKind.Poco;
            name = Named(poco.Name, name);
            return true;
        }

        if (type.GetCustomAttribute<QueryableViewAttribute>(inherit: false) is { } view)
        {
            kind = SourceKind.View;
            name = Named(view.Name, name);
            return true;
        }

        if (type.GetCustomAttribute<QueryableAttribute>(inherit: false) is { } queryable)
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
        type.HasAttribute<KeylessAttribute>();

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
                $"ScryOptions.{option} must be a plain collation name — letters, digits and underscores only. It is emitted into SQL rather than parameterized, so it has to be trusted configuration, never a value taken from a request or from anywhere a caller can influence.");
        }
    }

    /// <summary>
    /// The row policies a source carries, ordered base-most first. The type's own declaration is read
    /// first and then every base's in turn, which is the one place inheritance is deliberate: the
    /// opt-in attributes are declared-only (see <see cref="TryClassify"/>) so a subclass has to expose
    /// itself, but a policy it inherits is one it must not be able to shed by opting in.
    /// </summary>
    /// <remarks>
    /// A programmatic <c>AddPolicy</c> replaces the attribute on the same type, which is what it
    /// documents. It does not replace what a base declares — that would let registering a policy
    /// remove one — so both stay in the chain and both narrow.
    /// </remarks>
    static IReadOnlyList<Type> ResolvePolicies(Type type, string name, ScryOptions options)
    {
        List<Type> policies = [];
        for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
        {
            // inherit: false, because the walk is done here. Reading the attribute inheritably would
            // find a base's policy again at every level below it and apply it once per level.
            var policy = options.Policies.GetValueOrDefault(candidate) ??
                         candidate.GetCustomAttribute<ReturnableWithAttribute>(inherit: false)?.Policy;
            if (policy is null)
            {
                continue;
            }

            // The policy filters the type it was written against, which the executor widens the query
            // to. One written against something outside this hierarchy has no rows here to filter.
            var entityType = RowPolicy.EntityType(policy);
            if (!entityType.IsAssignableFrom(type))
            {
                throw new(
                    $"Row policy '{policy.Name}' on '{candidate.Name}' filters '{entityType.Name}', which source '{name}' does not derive from. A policy has to be written against the type it is attached to, or one of its bases.");
            }

            policies.Add(policy);
        }

        // Collected derived-first and applied base-first, so narrowing to a subclass filters exactly as
        // the base it narrows from would have — the invariant the OfType operator relies on.
        policies.Reverse();
        return policies;
    }

    /// <summary>The attachment members a type exposes, its inherited ones included.</summary>
    static IEnumerable<Member> Attachments(TypeMeta meta) =>
        meta.Members.Values.Where(_ => _.Kind == MemberKind.Attachment);

    /// <summary>
    /// The members forming a row's primary key, derived from the annotations and EF's own naming
    /// conventions: <c>[Key]</c> where written, else a member named <c>Id</c>, else one named
    /// <c>{TypeName}Id</c>. Ordinal by name, which is the order attachment keys travel in.
    /// </summary>
    /// <remarks>
    /// Must stay in lockstep with <c>MetadataModelReader.Keys</c>, which repeats this over metadata to
    /// generate the client. Deriving rather than reading the EF key is what keeps the two able to
    /// agree at all — the generator never runs the model, so fluent configuration is invisible to it —
    /// and <see cref="ValidateAgainstModel"/> verifies the answer against the real key at startup.
    /// </remarks>
    static IReadOnlyList<Member> DeriveKeys(TypeMeta meta)
    {
        // An attachment is not a value and a navigation is not one either, so neither can be a key.
        var candidates = meta.Members.Values
            .Where(_ => _.Kind == MemberKind.Scalar)
            .ToList();

        var declared = candidates
            .Where(_ => _.Property.HasAttribute<KeyAttribute>())
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .ToList();
        if (declared.Count > 0)
        {
            return declared;
        }

        foreach (var convention in new[] {"Id", $"{meta.ClrType.Name}Id"})
        {
            if (candidates.FirstOrDefault(_ => string.Equals(_.Name, convention, StringComparison.Ordinal)) is { } match)
            {
                return [match];
            }
        }

        var attachment = Attachments(meta).First().Name;
        throw new(
            $"'{meta.ClrType.Name}.{attachment}' is an [Attachment], but no primary key could be derived for '{meta.ClrType.Name}'. An attachment is fetched by its row's key, so one has to be nameable by a client: mark the key member(s) with [Key], or name a member 'Id' or '{meta.ClrType.Name}Id'. The member must also be exposed — a key a client cannot read is one it cannot send back.");
    }

    /// <summary>
    /// The attachment check a source carries, or null where it exposes no attachment. Refuses a source
    /// exposing one with no check: the fetch endpoint is reached by key, so leaving it unauthorized
    /// would serve any row whose key can be guessed.
    /// </summary>
    static Type? ResolveAttachmentPolicy(Schema schema, Type type, string name, SourceKind kind, ScryOptions options)
    {
        var meta = schema.types[type];
        if (Attachments(meta).FirstOrDefault() is not { } attachment)
        {
            return null;
        }

        // A view and a POCO have no key: a keyless view is not addressable by row, and a POCO has no
        // table to read one back from. The same reasoning already disables cursor paging for both.
        if (kind != SourceKind.Entity)
        {
            throw new(
                $"'{type.Name}.{attachment.Name}' is an [Attachment], but source '{name}' is a {kind.ToString().ToLowerInvariant()} and has no primary key to fetch the value by. Only a [Queryable] entity can carry an attachment.");
        }

        // Walked like a row policy's chain, and for the same reason: a subclass must not be able to
        // shed the check its base declared by opting itself in.
        for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
        {
            var policy = options.AttachmentPolicies.GetValueOrDefault(candidate) ??
                         candidate.GetCustomAttribute<AttachmentWithAttribute>(inherit: false)?.Policy;
            if (policy is null)
            {
                continue;
            }

            var entityType = AttachmentPolicy.EntityType(policy);
            if (!entityType.IsAssignableFrom(type))
            {
                throw new(
                    $"Attachment policy '{policy.Name}' on '{candidate.Name}' authorizes '{entityType.Name}', which source '{name}' does not derive from. A policy has to be written against the type it is attached to, or one of its bases.");
            }

            return policy;
        }

        throw new(
            $"'{type.Name}.{attachment.Name}' is an [Attachment], but '{type.Name}' has no attachment policy. An attachment is fetched by row key through an endpoint of its own, so it stays unreadable until something authorizes it: add [AttachmentWith(typeof(...))] or ScryOptions.AddAttachmentPolicy, implementing IAttachmentPolicy<{type.Name}>.");
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
                property.HasAttribute<QueryIgnoreAttribute>())
            {
                EnsureNoPreviousNames(type, property);
                EnsureNoBinaryTransfer(type, property, "which is not exposed to clients. Remove it, or remove whatever excludes the member.");
                EnsureNoAttachment(type, property, "which is not exposed to clients. Remove it, or remove whatever excludes the member.");
                continue;
            }

            if (IsScalar(property.PropertyType))
            {
                if (property.PropertyType != typeof(byte[]))
                {
                    EnsureNoBinaryTransfer(type, property, $"which is a '{ScalarDisplay(property.PropertyType)}'. Only byte[] members can travel as binary parts.");
                    EnsureNoAttachment(type, property, $"which is a '{ScalarDisplay(property.PropertyType)}'. Only byte[] members can be attachments.");
                }

                if (property.HasAttribute<AttachmentAttribute>())
                {
                    // The two describe opposite fates for the same value: one encodes what the query
                    // read, the other means the query never read it.
                    if (property.HasAttribute<BinaryTransferAttribute>())
                    {
                        throw new($"'{type.Name}.{property.Name}' carries both [Attachment] and [BinaryTransfer]. [BinaryTransfer] changes how a value the query read is encoded; [Attachment] means the query never reads it. Keep one.");
                    }

                    meta.Members[property.Name] = new(property.Name, property, MemberKind.Attachment);
                    continue;
                }

                meta.Members[property.Name] = new(property.Name, property, MemberKind.Scalar);
                continue;
            }

            EnsureNoBinaryTransfer(type, property, "which is not a scalar member. Only byte[] members can travel as binary parts.");
            EnsureNoAttachment(type, property, "which is not a scalar member. Only byte[] members can be attachments.");

            // A reference navigation or a complex value type: traversable when the target is opted in.
            // Unwrap Nullable<T> so an optional struct complex member (Address?) resolves to Address.
            var target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (queryableTypes.Contains(target))
            {
                meta.Members[property.Name] = new(property.Name, property, MemberKind.Navigation);
                continue;
            }

            // A collection is exposed only when the member asks for it — default-deny applies to the
            // member, not just to the type — and its element must be something a query can already
            // read: an opted-in type, or a scalar (an EF primitive collection, whose elements are
            // values with no allow-list of their own to consult).
            if (property.HasAttribute<QueryableCollectionAttribute>() &&
                CollectionElement(property.PropertyType) is { } element &&
                (queryableTypes.Contains(element) || IsScalar(element)))
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

    static void EnsureNoBinaryTransfer(Type type, PropertyInfo property, string reason)
    {
        if (property.HasAttribute<BinaryTransferAttribute>())
        {
            throw new($"[BinaryTransfer] on '{type.Name}.{property.Name}', {reason}");
        }
    }

    static void EnsureNoAttachment(Type type, PropertyInfo property, string reason)
    {
        if (property.HasAttribute<AttachmentAttribute>())
        {
            throw new($"[Attachment] on '{type.Name}.{property.Name}', {reason}");
        }
    }

    /// <summary>
    /// Whether a type is one of the closed scalar set — the values a query may compare, order by,
    /// project, and hold in a collection of values.
    /// </summary>
    public static bool IsScalar(Type type)
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
