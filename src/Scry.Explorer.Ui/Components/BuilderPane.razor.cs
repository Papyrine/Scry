using Microsoft.AspNetCore.Components;

namespace Scry;

/// <summary>
/// The query builder pane. It holds no query of its own: it reads the editor's text on every change
/// (see <see cref="QueryBuilder.Read"/>), and every control raises an edit for the app to apply to
/// what the editor holds at that moment, which can be a keystroke ahead of the text this was drawn from.
/// </summary>
public partial class BuilderPane
{
    [Parameter]
    [EditorRequired]
    public SchemaIndex Index { get; set; } = null!;

    /// <summary>The active tab's query, as the editor last reported it.</summary>
    [Parameter]
    public string Query { get; set; } = "";

    /// <summary>Raised with an edit for the app to apply to the editor's text.</summary>
    [Parameter]
    public EventCallback<Func<string, string?>> OnEdit { get; set; }

    /// <summary>Raised with a new query to open the way the schema pane opens one: in a blank tab, or a new one.</summary>
    [Parameter]
    public EventCallback<string> OnStart { get; set; }

    // Read again only when the text or the schema changes: the app renders on every keystroke and
    // every frame of a pane drag, and a read parses the query.
    (string? Text, SchemaIndex? Index, BuilderRead Read) cache;

    // The value inputs whose last entry was not a value of their type, by input.
    readonly HashSet<string> invalid = [];

    string startSource = "";

    static readonly (string Terminal, string Label)[] terminals =
    [
        ("", "rows"),
        ("CountAsync", "count"),
        ("AnyAsync", "any"),
        ("FirstOrDefaultAsync", "first row")
    ];

    BuilderRead Read
    {
        get
        {
            if (!ReferenceEquals(cache.Text, Query) ||
                !ReferenceEquals(cache.Index, Index))
            {
                cache = (Query, Index, QueryBuilder.Read(Index, Query));
            }

            return cache.Read;
        }
    }

    BuilderQuery? Current => Read.Query;

    string? Problem => Read.Problem;

    protected override void OnParametersSet()
    {
        // The source a new query starts from stays the reader's choice until the schema stops having it.
        if (Index.Sources.All(_ => _.Name != startSource))
        {
            startSource = Index.Sources
                .Select(_ => _.Name)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault() ?? "";
        }
    }

    // A string rather than a bool: a bool would render the attribute bare, or not at all.
    static string Checked(bool value)
    {
        if (value)
        {
            return "true";
        }

        return "false";
    }

    Task Change(Func<BuilderQuery, BuilderQuery?> change) =>
        OnEdit.InvokeAsync(_ => QueryBuilder.Edit(Index, _, change));

    // What a changed input or select now holds. A method rather than inline in the markup, where the
    // empty string's quotes would end the attribute.
    static string Text(ChangeEventArgs args) =>
        args.Value?.ToString() ?? "";

    /// <summary>
    /// Starts a query on the chosen source. Into the editor itself when it is blank; into a tab of its
    /// own when it holds something the builder could not read, which is someone's work.
    /// </summary>
    Task Start()
    {
        if (startSource.Length == 0)
        {
            return Task.CompletedTask;
        }

        var snippet = QueryBuilder.Write(Index, new(startSource));
        if (Problem is null)
        {
            return OnEdit.InvokeAsync(_ => string.IsNullOrWhiteSpace(_) ? snippet : null);
        }

        return OnStart.InvokeAsync(snippet);
    }

    Task ChangeSource(object? value)
    {
        if (value?.ToString() is not { } source)
        {
            return Task.CompletedTask;
        }

        return Change(_ => QueryBuilder.ChangeSource(Index, _, source));
    }

    Task UseColumns() =>
        Change(_ => _ with {Projection = null});

    // ---- Filters ----

    Task SetFilterMember(int at, string display) =>
        Change(_ => WithFilterMember(_, at, display));

    /// <summary>
    /// Points a condition at another member. One of the same type keeps its comparison and its value;
    /// any other starts from its own type's defaults, since the old value would not compile against it.
    /// </summary>
    BuilderQuery? WithFilterMember(BuilderQuery query, int at, string display)
    {
        if (QueryBuilder.Model(Index, query) is not { } model ||
            at >= query.Filters.Count ||
            query.Filters[at] is not ComparisonFilter current ||
            QueryBuilder.Comparable(Index, model).FirstOrDefault(_ => _.Display == display) is not { } member)
        {
            return null;
        }

        var type = member.Member.TypeDisplay;
        var previous = QueryBuilder.Member(Index, model, current.Path)?.TypeDisplay;
        var filter = previous?.TrimEnd('?') == type.TrimEnd('?') &&
                     QueryBuilder.Operators(Index, type).Contains(current.Operator)
            ? current with
            {
                Path = member.Path
            }
            : QueryBuilder.Filter(Index, member);

        return query with
        {
            Filters = Replace(query.Filters, at, filter)
        };
    }

    Task SetFilterOperator(int at, string type, object? value)
    {
        if (!Enum.TryParse<FilterOperator>(value?.ToString(), out var comparison))
        {
            return Task.CompletedTask;
        }

        return Change(_ => WithFilterOperator(_, at, type, comparison));
    }

    // A comparison that takes a value keeps the one there was, or starts from the type's default.
    BuilderQuery? WithFilterOperator(BuilderQuery query, int at, string type, FilterOperator comparison)
    {
        if (at >= query.Filters.Count ||
            query.Filters[at] is not ComparisonFilter current)
        {
            return null;
        }

        var value = QueryBuilder.TakesValue(comparison)
            ? current.Value ?? QueryBuilder.DefaultValue(Index, type)
            : null;

        return query with
        {
            Filters = Replace(
                query.Filters,
                at,
                current with
                {
                    Operator = comparison,
                    Value = value
                })
        };
    }

    /// <summary>
    /// Writes what a value input holds when it is a value of the member's type. When it is not —
    /// letters in a number, the thirty-first of February — the query is left alone and the input says
    /// so, rather than writing code that does not compile.
    /// </summary>
    Task SetFilterValue(int at, string type, string key, string input)
    {
        if (QueryBuilder.Literal(Index, type, input) is not { } literal)
        {
            invalid.Add(key);
            return Task.CompletedTask;
        }

        invalid.Remove(key);
        return Change(_ => WithFilterValue(_, at, literal));
    }

    static BuilderQuery? WithFilterValue(BuilderQuery query, int at, string literal)
    {
        if (at >= query.Filters.Count ||
            query.Filters[at] is not ComparisonFilter current)
        {
            return null;
        }

        return query with
        {
            Filters = Replace(
                query.Filters,
                at,
                current with
                {
                    Value = literal
                })
        };
    }

    // ---- Sorts ----

    Task SetOrderMember(int at, string display) =>
        Change(_ => WithOrderMember(_, at, display));

    BuilderQuery? WithOrderMember(BuilderQuery query, int at, string display)
    {
        if (QueryBuilder.Model(Index, query) is not { } model ||
            at >= query.Orders.Count ||
            QueryBuilder.Comparable(Index, model).FirstOrDefault(_ => _.Display == display) is not { } member)
        {
            return null;
        }

        return query with
        {
            Orders = Replace(
                query.Orders,
                at,
                query.Orders[at] with
                {
                    Path = member.Path,
                    Code = null
                })
        };
    }

    Task SetDirection(int at, object? value) =>
        Change(_ => WithDirection(_, at, value?.ToString() == "descending"));

    static BuilderQuery? WithDirection(BuilderQuery query, int at, bool descending)
    {
        if (at >= query.Orders.Count)
        {
            return null;
        }

        return query with
        {
            Orders = Replace(
                query.Orders,
                at,
                query.Orders[at] with
                {
                    Descending = descending
                })
        };
    }

    // ---- Paging and the result ----

    /// <summary>A Skip or a Take: blank for none, a whole number of rows otherwise.</summary>
    Task SetCount(string id, string? text, bool skip)
    {
        int? count = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                invalid.Add(id);
                return Task.CompletedTask;
            }

            count = parsed;
        }

        invalid.Remove(id);
        if (skip)
        {
            return Change(_ => _ with {Skip = count});
        }

        return Change(_ => _ with {Take = count});
    }

    // What a query can be asked to answer with, and the terminal it already ends with when that is
    // another — ToListAsync, say — so the list can show it.
    static IEnumerable<(string Terminal, string Label)> Terminals(string? current)
    {
        if (current is null ||
            terminals.Any(_ => _.Terminal == current))
        {
            return terminals;
        }

        return [.. terminals, (current, current)];
    }

    Task SetTerminal(object? value) =>
        Change(_ => _ with {Terminal = value?.ToString() is {Length: > 0} terminal ? terminal : null});

    // ---- Shown values ----

    static string Label(FilterOperator comparison) =>
        comparison switch
        {
            FilterOperator.Equal => "==",
            FilterOperator.NotEqual => "!=",
            FilterOperator.LessThan => "<",
            FilterOperator.LessThanOrEqual => "<=",
            FilterOperator.GreaterThan => ">",
            FilterOperator.GreaterThanOrEqual => ">=",
            FilterOperator.Contains => "contains",
            FilterOperator.StartsWith => "starts with",
            FilterOperator.EndsWith => "ends with",
            FilterOperator.IsTrue => "is true",
            FilterOperator.IsFalse => "is false",
            FilterOperator.IsNull => "is null",
            _ => "is not null"
        };

    static string InputType(ValueKind kind) =>
        kind switch
        {
            ValueKind.Date => "date",
            ValueKind.DateTime or ValueKind.DateTimeOffset => "datetime-local",
            ValueKind.Time => "time",
            _ => "text"
        };

    static string? InputMode(ValueKind kind) =>
        kind switch
        {
            ValueKind.Integer => "numeric",
            ValueKind.Decimal or ValueKind.Double or ValueKind.Single => "decimal",
            _ => null
        };

    static string? Placeholder(ValueKind kind) =>
        kind switch
        {
            ValueKind.Duration => "d.hh:mm:ss",
            ValueKind.Guid => "00000000-0000-0000-0000-000000000000",
            _ => null
        };

    static IReadOnlyList<T> Replace<T>(IReadOnlyList<T> items, int at, T item)
    {
        var list = items.ToList();
        list[at] = item;
        return list;
    }

    static IReadOnlyList<T> Without<T>(IReadOnlyList<T> items, int at)
    {
        var list = items.ToList();
        if (at < list.Count)
        {
            list.RemoveAt(at);
        }

        return list;
    }
}
