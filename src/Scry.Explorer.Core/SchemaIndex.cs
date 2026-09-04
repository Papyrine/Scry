namespace Scry;

/// <summary>
/// A member's declared type as the schema pane shows it: the display text, and the model or enum it
/// links to when the schema describes one.
/// </summary>
public sealed record TypeReference(string Display, string? LinkTarget);

/// <summary>Where a member came from, when a type inherits.</summary>
public sealed record IndexedMember(ScryMemberInfo Member, string DeclaringModel);

/// <summary>A schema search hit.</summary>
public sealed record SchemaMatch(string Model, string? Member);

/// <summary>
/// <see cref="ScryIntrospection"/> arranged for browsing: lookups by name, the inheritance walked in
/// both directions, and the type-display spellings resolved to the models they name.
/// </summary>
/// <remarks>
/// The explorer already compiles this contract into C# through <see cref="ModelSynthesizer"/>; this is
/// the same contract read for display instead. It carries no descriptions because the contract does
/// not — a Scry member has a name, a type and a handful of flags, and that is the whole of what a
/// server publishes about it.
/// </remarks>
public sealed class SchemaIndex
{
    const string ListPrefix = "global::System.Collections.Generic.IReadOnlyList<";

    readonly Dictionary<string, ScryTypeInfo> types;
    readonly Dictionary<string, ScryEnumInfo> enums;
    readonly Dictionary<string, ScrySourceInfo> sourcesByModel;

    public ScryIntrospection Introspection { get; }

    public IReadOnlyList<ScrySourceInfo> Sources =>
        Introspection.Sources;

    public IReadOnlyList<ScryEnumInfo> Enums =>
        Introspection.Enums;

    public SchemaIndex(ScryIntrospection introspection)
    {
        Introspection = introspection;
        types = introspection.Types.ToDictionary(_ => _.Model, StringComparer.Ordinal);
        enums = introspection.Enums.ToDictionary(_ => _.Name, StringComparer.Ordinal);

        // Two sources can name the same model only if a host declared them so; the first wins, which
        // is the one a reader would find by browsing the source list top to bottom.
        sourcesByModel = [];
        foreach (var source in introspection.Sources)
        {
            sourcesByModel.TryAdd(source.Model, source);
        }
    }

    public ScryTypeInfo? Type(string model) =>
        types.GetValueOrDefault(model);

    public ScryEnumInfo? Enum(string name) =>
        enums.GetValueOrDefault(name);

    /// <summary>The source this model is queryable as, or null when it is only reachable as a navigation.</summary>
    public ScrySourceInfo? SourceFor(string model) =>
        sourcesByModel.GetValueOrDefault(model);

    /// <summary>
    /// Every member of a model, inherited ones first, each tagged with the model that declared it.
    /// Mirrors the walk the generated code is synthesized from, so what the pane lists is what a
    /// client would get.
    /// </summary>
    public IReadOnlyList<IndexedMember> AllMembers(string model)
    {
        var members = new List<IndexedMember>();
        Collect(model, members, []);
        return members;
    }

    /// <summary>The models that inherit from this one, so a base type links down as well as up.</summary>
    public IReadOnlyList<string> Derived(string model) =>
        [.. Introspection.Types.Where(_ => _.Base == model).Select(_ => _.Model).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A type-display spelling resolved for display: the <c>global::</c> prefixes trimmed, a collection
    /// unwrapped to what it holds, and a link where the remainder names a model or an enum.
    /// </summary>
    public TypeReference Resolve(string typeDisplay)
    {
        var display = typeDisplay;
        var suffix = "";

        if (display.StartsWith(ListPrefix, StringComparison.Ordinal) &&
            display.EndsWith('>'))
        {
            display = display[ListPrefix.Length..^1];
            suffix = "[]";
        }

        var nullable = display.EndsWith('?');
        if (nullable)
        {
            display = display[..^1];
        }

        var target = types.ContainsKey(display) || enums.ContainsKey(display) ? display : null;
        var trimmed = display.StartsWith("global::", StringComparison.Ordinal) ? display["global::".Length..] : display;
        return new(trimmed + (nullable ? "?" : "") + suffix, target);
    }

    /// <summary>
    /// Sources, models, and members whose name contains <paramref name="term"/>, matches inside
    /// <paramref name="within"/> first so a search made while reading a type answers about that type
    /// before the rest of the schema.
    /// </summary>
    public IReadOnlyList<SchemaMatch> Search(string? term, string? within = null, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var matches = new List<SchemaMatch>();
        foreach (var type in Introspection.Types)
        {
            if (type.Model.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new(type.Model, null));
            }

            matches.AddRange(
                type.Members
                    .Where(_ => _.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Select(_ => new SchemaMatch(type.Model, _.Name)));
        }

        return
        [
            .. matches
                .OrderByDescending(_ => within is not null && _.Model == within)
                .ThenBy(_ => _.Model, StringComparer.Ordinal)
                .ThenBy(_ => _.Member, StringComparer.Ordinal)
                .Take(limit)
        ];
    }

    /// <summary>
    /// A starter query for a source: every scalar member it can project, and a nested object for each
    /// navigation carrying that model's scalars in turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A navigation is projected *into* rather than named as a leaf, which is the only way a query can
    /// carry one — and the shape a reader is most likely to want next, since a row's own foreign key
    /// says less than the row it points at.
    /// </para>
    /// <para>
    /// Two kinds never appear because a projection cannot carry them. A collection is aggregable but,
    /// in the server's words, "neither traversable nor projectable"; an attachment has no value in a
    /// result at all; and both are published with <c>IsNavigation</c> false, so each has to be
    /// recognised on its own terms. Missing that is a query the editor compiles and the server then
    /// rejects.
    /// </para>
    /// <para>
    /// Two more are left out by choice. A sensitive member projects happily, answering
    /// <c>no-store</c> — but a suggested query should not put a password on screen by default. And a
    /// <c>byte[]</c> is bulk bytes whichever way it travels, which is a poor thing to open with.
    /// </para>
    /// <para>
    /// One level deep, so a self-navigation terminates and a model reached two ways is not spelled out
    /// twice.
    /// </para>
    /// </remarks>
    public string StarterQuery(ScrySourceInfo source)
    {
        // Scalars first, then the navigations. A nested object is several lines tall, and burying the
        // row's own columns between two of them makes the shorter half the harder to read.
        var scalars = new List<string>();
        var nested = new List<string>();
        foreach (var indexed in AllMembers(source.Model))
        {
            var member = indexed.Member;
            if (!Suggestable(member))
            {
                continue;
            }

            if (!member.IsNavigation)
            {
                scalars.Add($"_.{member.Name}");
            }
            else if (Nested(member) is { } projection)
            {
                nested.Add(projection);
            }
        }

        var members = scalars.Concat(nested).ToList();
        if (members.Count == 0)
        {
            return $"Query.{source.Name}";
        }

        // Composed compactly and printed, so what this offers and what the format button produces are
        // the same shape by construction.
        return QueryPrinter.Format($"Query.{source.Name}.Select(_ => new {{ {string.Join(", ", members)} }})");
    }

    // The navigation as an object of its own, or null when the model it points at has no scalar worth
    // carrying — an empty `new { }` is not a projection the server would accept.
    string? Nested(ScryMemberInfo member)
    {
        var reference = Resolve(member.TypeDisplay);
        if (reference.LinkTarget is not { } model ||
            Type(model) is null)
        {
            return null;
        }

        // The navigation is declared nullable, so reading through it warns without this. The generated
        // client spells it the same way.
        var access = $"_.{member.Name}{(member.TypeDisplay.EndsWith('?') ? "!" : "")}";

        var scalars = AllMembers(model)
            .Select(_ => _.Member)
            .Where(_ => Suggestable(_) && !_.IsNavigation)
            .Select(_ => $"{access}.{_.Name}")
            .ToList();

        return scalars.Count == 0 ? null : $"{member.Name} = new {{ {string.Join(", ", scalars)} }}";
    }

    /// <summary>Whether a starter query should offer this member. See <see cref="StarterQuery"/>.</summary>
    /// <remarks>
    /// Byte arrays go by their declared type rather than by a flag, because the contract publishes
    /// none: <c>[BinaryTransfer]</c> deliberately does not change the queryable surface — that is the
    /// whole of what the attribute claims, and why an attachment moves the schema stamp and a diverted
    /// <c>byte[]</c> does not. So a suggested query cannot tell a diverted one from an inline one, and
    /// has no reason to: both are bulk bytes, and the inline one is the worse of the two to open with.
    /// </remarks>
    static bool Suggestable(ScryMemberInfo member) =>
        !member.IsCollection &&
        !member.IsAttachment &&
        !member.IsSensitive &&
        member.TypeDisplay.TrimEnd('?') != "byte[]";

    void Collect(string model, List<IndexedMember> members, HashSet<string> seen)
    {
        if (!seen.Add(model) ||
            Type(model) is not { } type)
        {
            return;
        }

        if (type.Base is not null)
        {
            Collect(type.Base, members, seen);
        }

        members.AddRange(type.Members.Select(_ => new IndexedMember(_, model)));
    }
}
