using Microsoft.AspNetCore.Components;

namespace Scry;

public partial class SchemaPane
{
    [Parameter]
    [EditorRequired]
    public SchemaIndex Index { get; set; } = null!;

    [Parameter]
    public EventCallback<string> OnInsertQuery { get; set; }

    readonly List<string?> stack = [];
    string? current;
    string? search;

    IReadOnlyList<SchemaMatch> Matches =>
        Index.Search(search, current);

    bool HasMatches => Matches.Count > 0;

    string Previous =>
        stack[^1] ?? "Sources";

    bool CanGoBack => stack.Count > 0;

    string Placeholder
    {
        get
        {
            if (current is null)
            {
                return "Search the schema…";
            }

            return $"Search {current}…";
        }
    }

    // The sources by kind, the kinds and the sources within each in ordinal order.
    IEnumerable<IGrouping<string, ScrySourceInfo>> SourceGroups =>
        Index.Sources
            .GroupBy(_ => _.Kind)
            .OrderBy(_ => _.Key, StringComparer.Ordinal);

    static IEnumerable<ScrySourceInfo> Ordered(IEnumerable<ScrySourceInfo> group) =>
        group.OrderBy(_ => _.Name, StringComparer.Ordinal);

    bool HasCommands => Index.Commands.Count > 0;

    bool HasEnums => Index.Enums.Count > 0;

    IEnumerable<ScryEnumInfo> Enums =>
        Index.Enums.OrderBy(_ => _.Name, StringComparer.Ordinal);

    static string Values(ScryEnumInfo info) =>
        string.Join(", ", info.Values);

    string UrlLimit
    {
        get
        {
            var limit = Index.Introspection.QueryUrlLimit;
            if (limit == 0)
            {
                return "no GET route";
            }

            return $"{limit} chars";
        }
    }

    string SqlPreview
    {
        get
        {
            if (Index.Introspection.SqlPreview)
            {
                return "available";
            }

            return "off";
        }
    }

    static string MatchText(SchemaMatch match)
    {
        if (match.Member is null)
        {
            return match.Model;
        }

        return $"{match.Model} · {match.Member}";
    }

    // Null where nothing derives from the model, which is when the line is left out.
    IReadOnlyList<string>? DerivedFrom(string model)
    {
        var derived = Index.Derived(model);
        if (derived.Count == 0)
        {
            return null;
        }

        return derived;
    }

    // Each member with what its type resolves to, which is a link where the schema describes the type.
    IEnumerable<(ScryMemberInfo Member, TypeReference Reference, string DeclaringModel)> Members(string model) =>
        Index.AllMembers(model)
            .Select(_ => (_.Member, Index.Resolve(_.Member.TypeDisplay), _.DeclaringModel));

    static bool IsKey(ScryTypeInfo type, ScryMemberInfo member) =>
        type.Keys?.Contains(member.Name) == true;

    bool Inherited(string declaringModel) =>
        declaringModel != current;

    // Null where no command targets the model, which is when the section is left out.
    IReadOnlyList<ScryCommandInfo>? CommandsOn(string model)
    {
        var commands = Index.CommandsTargeting(model);
        if (commands.Count == 0)
        {
            return null;
        }

        return commands;
    }

    Task InsertQuery(ScrySourceInfo source) =>
        OnInsertQuery.InvokeAsync(Index.StarterQuery(source));

    void Go(string model)
    {
        // The search box is what got you here; clearing it puts the page you navigated to on screen
        // rather than leaving the result list over it.
        search = null;
        stack.Add(current);
        current = model;
    }

    void Pop()
    {
        current = stack[^1];
        stack.RemoveAt(stack.Count - 1);
    }
}
