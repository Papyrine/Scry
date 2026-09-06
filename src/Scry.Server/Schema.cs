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

    // The policied sources keyed by the CLR type a navigation would land on. Only the policied ones:
    // this exists so a traversal can ask whether the type it is stepping into filters its rows, and a
    // source with no policy is nothing for that question to find.
    readonly Dictionary<Type, ScrySource> policiedSources = [];

    // Every source keyed by its CLR type, which is how the denied-row probe finds the rows that own a
    // traversal. Kept apart from the name lookup above: a source answers to previous names too, and a
    // type is exactly one source.
    readonly Dictionary<Type, ScrySource> sourcesByType = [];

    // Previous wire names still answered to, kept apart from the current surface above so they never
    // leak into introspection or the stamp. Enum values are keyed by enum type, then previous name.
    readonly Dictionary<string, ScrySource> sourcePreviousNames = new(StringComparer.Ordinal);
    readonly Dictionary<Type, Dictionary<string, string>> enumPreviousNames = [];

    // Captured for the startup guardrail (ValidateAgainstModel): the CLR types the annotations claim
    // are EF-mapped entities/views versus the ones claimed to be complex value types. The classifiers
    // work from attributes alone; only the live EF model can confirm the claim is right.
    readonly List<Type> entitySourceTypes = [];
    readonly List<Type> complexTypes = [];

    // The cached row policies, for the facade a host invalidates and primes through.
    readonly List<CachedPolicyRegistration> cachedPolicies = [];

    /// <summary>Every cached row policy registered, or nothing where none is.</summary>
    internal IReadOnlyList<CachedPolicyRegistration> CachedPolicies => cachedPolicies;

    public bool TryGetSource(string name, [MaybeNullWhen(false)] out ScrySource source) =>
        sources.TryGetValue(name, out source) ||
        sourcePreviousNames.TryGetValue(name, out source);

    public bool TryGetType(Type type, [MaybeNullWhen(false)] out TypeMeta meta) =>
        types.TryGetValue(type, out meta);

    /// <summary>
    /// The source for <paramref name="type"/> where it carries a row policy. A navigation into such a
    /// type is rewritten to read through that policy (see <see cref="NavigationPolicy"/>) rather than
    /// straight off the owner, which is what keeps a policy that filters a source from being walked
    /// around by naming the source as another type's member.
    /// </summary>
    public bool TryGetPoliciedSource(Type type, [MaybeNullWhen(false)] out ScrySource source) =>
        policiedSources.TryGetValue(type, out source);

    /// <summary>
    /// The source a CLR type is exposed as, where it is one at all. A complex type is not, which is
    /// what a caller asking for the rows that own a traversal has to be able to find out.
    /// </summary>
    public bool TryGetSourceForType(Type type, [MaybeNullWhen(false)] out ScrySource source) =>
        sourcesByType.TryGetValue(type, out source);

    /// <summary>Every policied source, for the startup probe that translates each one's rewrite.</summary>
    internal IEnumerable<ScrySource> PoliciedSources => policiedSources.Values;

    /// <summary>Every allow-listed type, for a reader that has no path to resolve one by.</summary>
    internal IEnumerable<TypeMeta> Types => types.Values;

    /// <summary>Every source, for the startup check that each of its policies can be constructed.</summary>
    internal IEnumerable<ScrySource> Sources => sources.Values;

    /// <summary>
    /// How a type is named to a client: a source's wire name, or the model name introspection
    /// publishes for a type that is not one. What a rejection names, so the message never carries a
    /// CLR name a <c>Name</c> override was chosen to keep off the wire.
    /// </summary>
    public string WireName(Type type)
    {
        if (sourcesByType.TryGetValue(type, out var source))
        {
            return source.Name;
        }

        return $"{type.Name}QueryModel";
    }

    /// <summary>
    /// The sources whose rows may depend on who asked, each with why: one carrying a row or
    /// attachment policy, and a POCO source supplied by a factory, which is given the request's
    /// services. Ordered by name so a startup message names the same one every run.
    /// </summary>
    internal IEnumerable<CallerDependence> CallerDependentSources
    {
        get
        {
            foreach (var source in sources.Values.OrderBy(_ => _.Name, StringComparer.Ordinal))
            {
                if (source.Policies.Count > 0 ||
                    source.AttachmentPolicy is not null)
                {
                    yield return new(source.Name, "carries a policy, so its rows depend on who asked", Hint: null);
                }
                else if (source.FactorySupplied)
                {
                    yield return new(
                        source.Name,
                        "is supplied by a factory, which is given the request's services and may answer by who asked",
                        "Data that is the same for every caller is registered as the collection itself, which asks nothing of the caller.");
                }
            }
        }
    }

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

    /// <summary>
    /// Whether any allow-listed member anywhere carries <c>[BinaryTransfer]</c>. False for almost every
    /// model, and the only form of the question a batch can ask.
    /// </summary>
    /// <remarks>
    /// A single query decides whether it may spill from its own projection plan, which is exact. A batch
    /// cannot: it commits to one framing for the whole envelope before the first entry runs, and only
    /// entry n's own plan says whether entry n diverts — so entry 1 draining would be a bet that no
    /// later entry produces a part the drained bytes would have had to precede. This answers it the one
    /// way that is sound up front, at the cost of holding a whole batch whole on any model that has a
    /// binary member at all.
    /// </remarks>
    public bool CarriesBinary { get; private set; }

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
            SchemaStamp = Stamp,
            QueryUrlLimit = options.QueryUrlLimit
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
            enumInfos.Select(_ => (_.Name, _.Underlying, _.IsFlags, StampEnumMembers(_))).ToList());
    }

    static List<(string Name, string Value)> StampEnumMembers(ScryEnumInfo enumeration) =>
        enumeration.Values
            .Zip(enumeration.Constants!, (name, value) => (name, value))
            .ToList();

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
            members.Add(("~keys", string.Join(' ', keys)));
        }

        if (Sensitivity(type) is { Length: > 0 } sensitive)
        {
            members.Add(("~sensitive", sensitive));
        }

        return members;
    }

    /// <summary>
    /// The sensitivity line a type contributes to the stamp: the members it marks, and <c>*</c> where
    /// the type itself is marked. Present only where something is, so a model that marks nothing hashes
    /// exactly as it did before <c>[Sensitive]</c> existed.
    /// </summary>
    /// <remarks>
    /// Hashed — unlike <c>[Obsolete]</c> — because it changes what an already-deployed client may do
    /// rather than only what it should be told. A client generated before a member was marked keeps
    /// asking in URLs and is refused; moving the stamp is what turns that into a reported staleness
    /// with a regenerate to fix it. <c>*</c> is not a member name, so it cannot collide with one.
    /// </remarks>
    static string Sensitivity(ScryTypeInfo type)
    {
        var names = type.Members
            .Where(_ => _.IsSensitive)
            .Select(_ => _.Name)
            .ToList();
        if (type.IsSensitive)
        {
            names.Insert(0, "*");
        }

        return string.Join(' ', names);
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
                IsSensitive = meta.Sensitive,
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
            Obsolete = ObsoleteOf(member.Property),
            IsSensitive = member.Sensitive
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
            // The member's own unwrap: an optional struct complex member displays as its model, not
            // as Nullable`1.
            return new(member.Name, $"{member.Target.Name}QueryModel?", NeedsNullDefault: false, IsNavigation: true);
        }

        // An attachment is emitted as the handle rather than as the byte[] it is declared as, which is
        // exactly why — unlike [BinaryTransfer] — it moves the schema stamp. Mirrors ScryGenerator.Display.
        if (member.Kind == MemberKind.Attachment)
        {
            return new(member.Name, "global::Scry.ScryAttachment", NeedsNullDefault: true, IsNavigation: false)
            {
                IsAttachment = true,
                ContentType = member.ContentType
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
            enums.TryAdd(actual.Name, DescribeEnum(actual));
            if (nullable)
            {
                return $"{actual.Name}?";
            }

            return actual.Name;
        }

        var display = ScalarDisplay(actual);
        if (display is "string" or "byte[]")
        {
            return display;
        }

        if (nullable)
        {
            return $"{display}?";
        }

        return display;
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
    // Names and values come back in the same order from both calls, so they pair positionally. The
    // value is spelled as the metadata side spells it — the underlying integer, invariant culture —
    // since the two descriptions have to hash identically. Must stay in lockstep with
    // MetadataModelReader.CollectEnum.
    static ScryEnumInfo DescribeEnum(Type type) =>
        new(type.Name, Enum.GetNames(type))
        {
            Constants = Enum.GetValuesAsUnderlyingType(type)
                .Cast<object>()
                .Select(_ => Convert.ToString(_, CultureInfo.InvariantCulture)!)
                .ToList(),
            IsFlags = type.IsDefined(typeof(FlagsAttribute), inherit: false),
            Underlying = ScalarDisplay(Enum.GetUnderlyingType(type))
        };

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

        if (options.QueryUrlLimit < 0)
        {
            throw new($"ScryOptions.{nameof(options.QueryUrlLimit)} must be zero or greater. Zero maps no GET route; any other value is the longest encoded query a client is asked to keep in a URL.");
        }

        var schema = new Schema();
        var found = new List<(Type Type, string Name, SourceKind Kind)>();

        foreach (var type in contextType.Assembly.GetTypes())
        {
            EnsureOneOptIn(type);
            if (TryClassify(type, out var kind, out var name))
            {
                EnsureNameIsIdentifier(type, name);
                found.Add((type, name, kind));
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
        var queryableTypes = found.Select(_ => _.Type).Concat(schema.complexTypes).ToHashSet();

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

        // Pass 1c: build the cached policies' adapters, which need the member metadata above to derive
        // the key their answers are remembered by. One adapter per registration however many sources
        // carry it, since what it holds — the answers, and the gate serializing the work to produce
        // them — is exactly what those sources have to share.
        var cached = BuildCachedPolicies(schema, options);

        // Resolved here rather than during discovery: a cached policy's adapter is part of the chain,
        // and building one needs the metadata that pass 1 produced.
        var discovered = found
            .Select(_ => (_.Type, _.Name, _.Kind, Policies: ResolvePolicies(_.Type, _.Name, options, cached)))
            .ToList();

        // Pass 2: register each source with its resolver. Complex types are deliberately absent.
        foreach (var (type, name, kind, policies) in discovered)
        {
            if (schema.sources.ContainsKey(name))
            {
                throw new($"Duplicate queryable source name '{name}'.");
            }

            var source = new ScrySource(name, type, kind, policies, BuildResolver(name, type, kind, options))
            {
                AttachmentPolicy = ResolveAttachmentPolicy(schema, type, name, kind, options),
                FactorySupplied = kind == SourceKind.Poco && options.FactoryPocoSources.Contains(type)
            };
            schema.sources[name] = source;
            schema.sourcesByType[type] = source;
            if (policies.Count > 0)
            {
                schema.policiedSources[type] = source;
            }
        }

        // Pass 2b: derive the key every attachment is fetched by. Deferred until the member metadata
        // and base links exist, since a key declared on a base is still the derived row's.
        foreach (var meta in schema.types.Values)
        {
            if (Attachments(meta).Any())
            {
                meta.AttachmentKeys = DeriveAttachmentKeys(meta);
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

        // A row policy filters a source, and a subquery over a collection has none of its own, so an
        // aggregate over a policied type counts exactly the rows the policy exists to hide unless the
        // subquery is read through that policy. Which of the two a host wants is its call, and the
        // default is neither: refuse the member at startup rather than guess at query time.
        var policied = discovered
            .Where(_ => _.Policies.Count > 0)
            .ToDictionary(_ => _.Type, _ => _.Policies);
        foreach (var (owner, member) in schema.types.Values
                     .SelectMany(meta => meta.Members.Values.Select(member => (meta.ClrType, member)))
                     .Where(_ => _.member.Kind == MemberKind.Collection))
        {
            var element = CollectionElement(member.Type)!;
            if (!policied.TryGetValue(element, out var elementPolicies))
            {
                continue;
            }

            // Any one refusal refuses the member: the policies all narrow, so a chain is only as
            // readable through a collection as its least permissive link says it is.
            if (elementPolicies.FirstOrDefault(_ => _.Handling.CollectionNavigation == DeniedCollectionMode.Refuse) is { Policy: not null } refusing)
            {
                throw new(
                    $"'{owner.Name}.{member.Name}' is a [QueryableCollection] of '{element.Name}', which carries row policy '{refusing.Policy.Name}'. Aggregating it would count rows that policy hides. Set CollectionNavigation on the policy — Hide reads the collection through it, Error fails a query that would have skipped a denied row — or remove the attribute.");
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
        schema.CarriesBinary = schema.types.Values
            .SelectMany(_ => _.Members.Values)
            .Any(_ => _.BinaryTransfer);
        schema.Sensitive = new(schema);
        schema.Stamp = schema.ComputeStamp();
        return schema;
    }

    /// <summary>
    /// The resolver the sensitivity walk asks, built with the schema: whether a member path off a
    /// source is one the model marked <c>[Sensitive]</c>.
    /// </summary>
    internal SensitiveSchema Sensitive { get; private set; } = null!;

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

    // A type opts in as exactly one thing. TryClassify reads the attributes in an order of its own and
    // the generator in another, so a type carrying two would be one kind here and another there, with
    // every client reporting itself stale as the only symptom. The generator refuses it as SCRY008.
    internal static void EnsureOneOptIn(Type type)
    {
        List<string>? optIns = null;
        if (type.HasAttribute<QueryableAttribute>(inherit: false))
        {
            (optIns ??= []).Add("[Queryable]");
        }

        if (type.HasAttribute<QueryableViewAttribute>(inherit: false))
        {
            (optIns ??= []).Add("[QueryableView]");
        }

        if (type.HasAttribute<QueryablePocoAttribute>(inherit: false))
        {
            (optIns ??= []).Add("[QueryablePoco]");
        }

        if (type.HasAttribute<QueryableComplexAttribute>(inherit: false))
        {
            (optIns ??= []).Add("[QueryableComplex]");
        }

        if (optIns is {Count: > 1})
        {
            throw new($"'{type.Name}' carries {string.Join(" and ", optIns)}. A type opts in as exactly one of [Queryable], [QueryableView], [QueryablePoco], or [QueryableComplex].");
        }
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
    static IReadOnlyList<PolicyUse> ResolvePolicies(
        Type type,
        string name,
        ScryOptions options,
        IReadOnlyDictionary<Type, PolicyUse> cached)
    {
        List<PolicyUse> policies = [];
        for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
        {
            // Both kinds at each level, and the level's own before anything its base declares is
            // reached: a derived source's chain has to extend its base's rather than interleave with
            // it, which is what lets narrowing apply only the levels the base has not.
            List<PolicyUse> declared = [];

            // inherit: false, because the walk is done here. Reading the attribute inheritably would
            // find a base's policy again at every level below it and apply it once per level.
            if (DeclaredPolicy(candidate, options) is { } use)
            {
                declared.Add(use);
            }

            if (cached.TryGetValue(candidate, out var adapter))
            {
                declared.Add(adapter);
            }

            foreach (var policy in declared)
            {
                // The policy filters the type it was written against, which the executor widens the
                // query to. One written against something outside this hierarchy has no rows to filter.
                var entityType = RowPolicy.EntityType(policy.Policy);
                if (!entityType.IsAssignableFrom(type))
                {
                    throw new(
                        $"Row policy '{policy.Policy.Name}' on '{candidate.Name}' filters '{entityType.Name}', which source '{name}' does not derive from. A policy has to be written against the type it is attached to, or one of its bases.");
                }

                policies.Add(policy);
            }
        }


        // Collected derived-first and applied base-first, so narrowing to a subclass filters exactly as
        // the base it narrows from would have — the invariant the OfType operator relies on.
        policies.Reverse();
        return policies;
    }

    /// <summary>
    /// Builds one adapter per registered cached policy: the ordinary row policy the rest of the server
    /// applies, holding the store its answers live in and the accessors for the key they are remembered
    /// by and the version that says a row needs deciding again.
    /// </summary>
    static IReadOnlyDictionary<Type, PolicyUse> BuildCachedPolicies(Schema schema, ScryOptions options)
    {
        Dictionary<Type, PolicyUse> adapters = [];
        foreach (var (entity, (policy, version, handling)) in options.CachedPolicies)
        {
            // A cached policy filters a source, and a type that is not one has no rows for it to
            // decide about — the same reason an ordinary policy on a complex type is refused.
            if (!schema.types.TryGetValue(entity, out var meta))
            {
                throw new($"Cached row policy '{policy.Name}' is registered against '{entity.Name}', which is not an opted-in source. Attach it to a [Queryable] type.");
            }

            var keys = DeriveKeys(meta);
            if (keys.Count != 1)
            {
                throw new(
                    $"Cached row policy '{policy.Name}' is registered against '{entity.Name}', whose key is {(keys.Count == 0 ? "not derivable" : $"made of {keys.Count} members")}. Answers are remembered per row by a single key value, so the type needs one key member — a [Key], an 'Id', or an '{entity.Name}Id'.");
            }

            var parameter = Expression.Parameter(entity, "e");
            var key = Expression.Lambda(Expression.Property(parameter, keys[0].Property), parameter);

            var registration = new CachedPolicyRegistration(entity, policy, options.CachedPolicyStore, options.MaxCachedPolicyKeys, options.MaxCachedPolicyRows);
            var adapter = typeof(CachedRowPolicyAdapter<,,>).MakeGenericType(entity, keys[0].Type, version.ReturnType);

            registration.Adapter = Activator.CreateInstance(adapter, registration, key, version)!;
            schema.cachedPolicies.Add(registration);
            adapters[entity] = new(adapter, handling)
            {
                Instance = registration.Adapter
            };
        }

        return adapters;
    }

    /// <summary>
    /// The policy one type declares for itself, programmatically or by attribute, with what its denied
    /// rows produce. Nothing where the type declares none — a base's is the caller's walk to make.
    /// </summary>
    static PolicyUse? DeclaredPolicy(Type type, ScryOptions options)
    {
        if (options.Policies.TryGetValue(type, out var registered))
        {
            return new(registered.Policy, registered.Handling);
        }

        if (type.GetCustomAttribute<ReturnableWithAttribute>(inherit: false) is not { } attribute)
        {
            return null;
        }

        return new(
            attribute.Policy,
            new()
            {
                RootSingle = attribute.RootSingle,
                RootList = attribute.RootList,
                Navigation = attribute.Navigation,
                CollectionNavigation = attribute.CollectionNavigation
            });
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

        var type = meta.ClrType.Name;
        foreach (var convention in new[] {"Id", $"{type}Id"})
        {
            if (candidates.FirstOrDefault(_ => string.Equals(_.Name, convention, StringComparison.Ordinal)) is { } match)
            {
                return [match];
            }
        }

        return [];
    }

    /// <summary>
    /// The same, for an attachment — which is what a key was first derived for, and so what the failure
    /// where none can be says.
    /// </summary>
    static IReadOnlyList<Member> DeriveAttachmentKeys(TypeMeta meta)
    {
        if (DeriveKeys(meta) is {Count: > 0} keys)
        {
            return keys;
        }

        var type = meta.ClrType.Name;
        var attachment = Attachments(meta).First().Name;
        throw new(
            $"'{type}.{attachment}' is an [Attachment], but no primary key could be derived for '{type}'. An attachment is fetched by its row's key, so one has to be nameable by a client: mark the key member(s) with [Key], or name a member 'Id' or '{type}Id'. The member must also be exposed — a key a client cannot read is one it cannot send back.");
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
    // Answered once per type: the walk below reflects over the type's interfaces, and the builder
    // asks about the same declared collection types on every request that flattens or aggregates one.
    static readonly ConcurrentDictionary<Type, Type?> collectionElements = new();

    public static Type? CollectionElement(Type type) =>
        collectionElements.GetOrAdd(type, FindCollectionElement);

    static Type? FindCollectionElement(Type type)
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

    internal static TypeMeta BuildTypeMeta(Type type, HashSet<Type> queryableTypes)
    {
        // Declared-only, like every other opt-in read here: a subclass of a sensitive type is not
        // itself sensitive unless it says so, matching what the metadata side can see.
        var meta = new TypeMeta(type)
        {
            Sensitive = type.HasAttribute<SensitiveAttribute>(inherit: false)
        };

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

            EnsureReadableByTheGenerator(type, property, queryableTypes);

            if (IsScalar(property.PropertyType))
            {
                EnsureEnumInModelAssembly(type, property, property.PropertyType);
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

                    var attachment = new Member(property.Name, property, MemberKind.Attachment);
                    ValidateContentType(type, attachment);
                    meta.Members[property.Name] = attachment;
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
            if (property.HasAttribute<QueryableCollectionAttribute>())
            {
                var element = ExposableCollectionElement(property.PropertyType);
                if (element is null &&
                    CollectionElement(property.PropertyType) is not null)
                {
                    throw new($"'{type.Name}.{property.Name}' is a '{property.PropertyType.Name}', a collection shape the generator does not read, so a client could never see the member and every client would report itself stale. Declare it as {CollectionShapes.Described}.");
                }

                if (element is not null &&
                    (queryableTypes.Contains(element) || IsScalar(element)))
                {
                    EnsureEnumInModelAssembly(type, property, element);
                    meta.Members[property.Name] = new(property.Name, property, MemberKind.Collection);
                    continue;
                }
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

    /// <summary>
    /// Refuses a member the generator could never read. A client is generated from the model
    /// assembly's metadata alone, so a member inherited from a base in another assembly is invisible
    /// to it — while reflection here would expose it, and every client would then report itself
    /// stale. A base in the model assembly is read on both sides whether or not it opted in, and an
    /// opted-in base is the generated model's own base, so neither is refused.
    /// </summary>
    static void EnsureReadableByTheGenerator(Type type, PropertyInfo property, HashSet<Type> queryableTypes)
    {
        var declaring = property.DeclaringType;
        if (declaring is null ||
            declaring == type ||
            declaring.Assembly == type.Assembly ||
            queryableTypes.Contains(declaring))
        {
            return;
        }

        throw new($"'{type.Name}.{property.Name}' is inherited from '{declaring.Name}' in assembly '{declaring.Assembly.GetName().Name}'. A client is generated from the model assembly's metadata alone, so it could never see the member, and every client would report itself stale. Declare the member on a type in the model assembly, or hide it by overriding it with [QueryIgnore].");
    }

    /// <summary>
    /// The same refusal for an enum: the generator re-emits one from the model assembly's metadata,
    /// and cannot read the members of one declared elsewhere.
    /// </summary>
    static void EnsureEnumInModelAssembly(Type type, PropertyInfo property, Type valueType)
    {
        var underlying = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (!underlying.IsEnum ||
            underlying.Assembly == type.Assembly)
        {
            return;
        }

        throw new($"'{type.Name}.{property.Name}' is a '{underlying.Name}', an enum declared in assembly '{underlying.Assembly.GetName().Name}'. A client re-emits an enum from the model assembly's metadata alone, so it could never see this one, and every client would report itself stale. Declare the enum in the model assembly, or exclude the member with [QueryIgnore].");
    }

    /// <summary>
    /// The element type of a collection declared in a shape the generator reads too — a
    /// one-dimensional array or one of <see cref="CollectionShapes.GenericDefinitions"/> — or null.
    /// Decides exposure, where <see cref="CollectionElement"/> reads any collection already exposed.
    /// </summary>
    internal static Type? ExposableCollectionElement(Type type)
    {
        if (type.IsArray)
        {
            if (type.GetArrayRank() == 1)
            {
                return type.GetElementType();
            }

            return null;
        }

        if (type.IsGenericType &&
            CollectionShapes.GenericDefinitions.Contains(type.GetGenericTypeDefinition().FullName!))
        {
            return type.GetGenericArguments()[0];
        }

        return null;
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

    /// <summary>
    /// Checks that a declared content type is one that can be sent. The value reaches a response
    /// header, so a line break in it would let the model author split the response — not a caller's
    /// input, but a mistake worth refusing at startup rather than serving.
    /// </summary>
    static void ValidateContentType(Type type, Member member)
    {
        if (member.ContentType is not { } contentType)
        {
            return;
        }

        if (IsMediaType(contentType))
        {
            return;
        }

        throw new($"'{type.Name}.{member.Name}' declares ContentType '{contentType}', which is not a media type. Write one as 'type/subtype' — for example 'image/png' — or leave it unset to serve the bytes as '{AttachmentMedia.Default}'.");
    }

    /// <summary>
    /// Whether a value is shaped as a media type — <c>type/subtype</c>, optionally with parameters,
    /// and nothing a header cannot carry. The same rule for what a model declares and for what an
    /// attachment policy replaces it with; only the first is checked at startup.
    /// </summary>
    internal static bool IsMediaType(string contentType)
    {
        var media = contentType.AsSpan();
        var index = media.IndexOf(';');
        if (index != -1)
        {
            media = media[..index];
        }

        media = media.Trim();
        return media.Length > 0 &&
               media.Count('/') == 1 &&
               media[0] != '/' &&
               media[^1] != '/' &&
               !contentType.Any(char.IsControl);
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
        string name,
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
        return (db, _) =>
        {
            // A type the context does not map is refused at startup unless the host said its
            // assembly serves several contexts; then a query naming one is a rejection here — the
            // same answer as a source that does not exist — rather than the fault Set<T>() would
            // raise, which a client could otherwise produce on demand.
            if (db.Model.FindEntityType(type) is null)
            {
                throw new ScryValidationException($"Unknown source '{name}'.");
            }

            return (IQueryable) typedSet.Invoke(db, null)!;
        };
    }
}
