namespace Scry;

/// <summary>
/// One fetchable attachment of a result: the source and member the bytes are claimed from, and where
/// in each row the key they are claimed by landed.
/// </summary>
/// <param name="Root">The source the bytes are claimed from, as the wire request names it.</param>
/// <param name="Member">The member on that source holding them.</param>
/// <param name="KeyColumns">
/// The result's own property names, in the order an <see cref="AttachmentRequest"/> wants its keys.
/// </param>
/// <param name="ContentType">
/// What the model says the bytes are, or null where it says nothing. Known before the fetch, which
/// is what lets a download be named for what it is rather than <c>.bin</c>.
/// </param>
public sealed record AttachmentLink(
    string Root,
    string Member,
    IReadOnlyList<string> KeyColumns,
    string? ContentType = null);

/// <summary>
/// Works out which attachments the rows of a result can be fetched by. A generated client learns
/// this while translating — its rows materialize into model types carrying <c>ScryAttachment</c>
/// handles — but the explorer renders the response as JSON and never materializes a row, so there is
/// no handle to open. This re-derives the same fact from the introspected schema and the wire
/// request, exactly as <see cref="ModelSynthesizer"/> re-derives the generated model.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ Lockstep with the client's own plan (<c>ScryQueryableExtensions.PlanFor</c> and
/// <c>QueryTranslator.ResolveAttachments</c>): the operators refused here are the ones refused
/// there, for the same reason.
/// </para>
/// <para>
/// A link is offered for every attachment the source declares, whether or not the snippet named one
/// — the explorer's rows are JSON, so there is no projected member to key it off. That widens no
/// access: the fetch is a request of its own, authorized by the member's own check and filtered by
/// the source's row policies, and this only saves the caller hand-writing it.
/// </para>
/// </remarks>
public static class AttachmentLinker
{
    public static IReadOnlyList<AttachmentLink> Link(ScryIntrospection introspection, QueryRequest request)
    {
        if (introspection.Sources.FirstOrDefault(_ => _.Name == request.Root) is not { } source ||
            Model(introspection, source.Model) is not { } type)
        {
            return [];
        }

        var attachments = Attachments(introspection, type);
        if (attachments.Count == 0)
        {
            return [];
        }

        // These rewrite what a row is — deduplicated, flattened, combined, or built from two sources
        // — so a key that came back with the row no longer identifies one row of the source.
        if (request.Pipeline.Any(_ => _ is DistinctOp or SelectManyOp or JoinOp or SetOp or GroupByOp))
        {
            return [];
        }

        // Carried on every type whose members hold an attachment, inherited ones included — so a type
        // that inherits its attachment carries the key it is fetched by rather than deferring to the
        // base for it. Same read as ModelSynthesizer's.
        if (type.Keys is not { Count: > 0 } keys)
        {
            return [];
        }

        var columns = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            // A key the query did not bring back leaves its rows unidentifiable, so nothing is
            // offered rather than a fetch that could only fail.
            if (Column(request, key) is not { } column)
            {
                return [];
            }

            columns.Add(column);
        }

        return [..attachments.Select(_ => new AttachmentLink(request.Root, _.Name, columns, _.ContentType))];
    }

    /// <summary>
    /// Where a key value landed in the result. A projection names it whatever the snippet called it,
    /// so it is matched by the member it reads rather than by name; a query that wrote none is keyed
    /// by the model's own names. Either way the response's properties are camel-cased by
    /// <see cref="ScryJson"/>'s dictionary policy, which is what the result table's columns are.
    /// </summary>
    static string? Column(QueryRequest request, string key)
    {
        // At most one: the validator refuses a second.
        if (request.Pipeline.OfType<SelectOp>().FirstOrDefault() is not { } select)
        {
            return Camel(key);
        }

        foreach (var member in select.Projection.Members)
        {
            // A leaf of the row itself. A key reached through a navigation belongs to that row rather
            // than this one, and a computed value is not a key at all.
            if (member.Value is NodeValue {Node: MemberNode {Path: [var read]}} &&
                string.Equals(read, key, StringComparison.Ordinal))
            {
                return Camel(member.Name);
            }
        }

        return null;
    }

    static ScryTypeInfo? Model(ScryIntrospection introspection, string model) =>
        introspection.Types.FirstOrDefault(_ => _.Model == model);

    // Declared plus inherited, matching ModelSynthesizer: an attachment declared on a base is the
    // derived row's too.
    static List<ScryMemberInfo> Attachments(ScryIntrospection introspection, ScryTypeInfo type)
    {
        var members = new List<ScryMemberInfo>();
        if (Base(introspection, type) is { } baseType)
        {
            members.AddRange(Attachments(introspection, baseType));
        }

        members.AddRange(type.Members.Where(_ => _.IsAttachment));
        return members;
    }

    static ScryTypeInfo? Base(ScryIntrospection introspection, ScryTypeInfo type)
    {
        if (type.Base is { } model)
        {
            return Model(introspection, model);
        }

        return null;
    }

    static string Camel(string name) =>
        JsonNamingPolicy.CamelCase.ConvertName(name);
}
