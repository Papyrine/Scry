namespace Scry;

/// <summary>
/// The query builder's reading and writing of a snippet, and the edits its pane makes in between.
/// </summary>
/// <remarks>
/// <para>
/// A snippet is read into a <see cref="BuilderQuery"/>, changed, and written back whole through
/// <see cref="QueryPrinter"/> — the format button's layout, which is the explorer's own — with the
/// variable declarations ahead of the query kept as written. A rewrite rather than a splice: a query
/// is one expression with one house layout, which the format button already imposes on demand.
/// </para>
/// <para>
/// What a rewrite would lose, the reader refuses to read rather than lose: a comment inside the
/// query, lambdas naming their parameter differently, an operator the builder does not write, or
/// operators in an order it would not write them in. What it reads but cannot show as rows — a
/// condition joined with <c>||</c>, a computed column — it carries as the code it was written as.
/// </para>
/// </remarks>
public static partial class QueryBuilder
{
    /// <summary>The model a query's source yields, or null where the server no longer publishes the source.</summary>
    public static string? Model(SchemaIndex index, BuilderQuery query) =>
        index.Sources.FirstOrDefault(_ => _.Name == query.Source)?.Model;

    /// <summary>
    /// The member a path names, read through the navigations ahead of it, or null where a step does not
    /// resolve — a member the model does not have, or one that is not a navigation with more to come.
    /// </summary>
    public static ScryMemberInfo? Member(SchemaIndex index, string model, IReadOnlyList<string> path)
    {
        ScryMemberInfo? member = null;
        var current = model;
        for (var position = 0; position < path.Count; position++)
        {
            if (current is null ||
                Find(index, current, path[position]) is not { } found)
            {
                return null;
            }

            member = found;
            if (position < path.Count - 1)
            {
                if (!found.IsNavigation)
                {
                    return null;
                }

                current = Target(index, found);
            }
        }

        return member;
    }

    static ScryMemberInfo? Find(SchemaIndex index, string model, string name) =>
        index.AllMembers(model)
            .Select(_ => _.Member)
            .FirstOrDefault(_ => _.Name == name);

    /// <summary>The model a navigation points at, or null for a member that does not point at one.</summary>
    public static string? Target(SchemaIndex index, ScryMemberInfo member)
    {
        if (index.Resolve(member.TypeDisplay).LinkTarget is { } target &&
            index.Type(target) is not null)
        {
            return target;
        }

        return null;
    }

    /// <summary>
    /// The members a filter or a sort key can name: the model's own comparable scalars, then those of
    /// each model its navigations point at — one level through, far enough for the department an
    /// employee works in without listing a manager's manager's manager.
    /// </summary>
    public static IReadOnlyList<BuilderMember> Comparable(SchemaIndex index, string model)
    {
        var own = new List<BuilderMember>();
        var through = new List<BuilderMember>();
        foreach (var member in index.AllMembers(model).Select(_ => _.Member))
        {
            if (!member.IsNavigation)
            {
                if (IsComparable(index, member))
                {
                    own.Add(new([member.Name], member));
                }

                continue;
            }

            if (Target(index, member) is not { } target)
            {
                continue;
            }

            through.AddRange(
                index.AllMembers(target)
                    .Select(_ => _.Member)
                    .Where(_ => IsComparable(index, _))
                    .Select(_ => new BuilderMember([member.Name, _.Name], _)));
        }

        return [.. own, .. through];
    }

    // An attachment has no value to compare, and a collection is aggregated rather than compared.
    static bool IsComparable(SchemaIndex index, ScryMemberInfo member) =>
        member is {IsNavigation: false, IsCollection: false, IsAttachment: false} &&
        Kind(index, member.TypeDisplay) != ValueKind.None;

    /// <summary>
    /// The members a projection can carry from a model, in the order the model declares them: all but a
    /// collection, which is aggregable rather than projectable, and an attachment, which no query reads.
    /// A navigation is among them, to be projected into.
    /// </summary>
    public static IReadOnlyList<ScryMemberInfo> Projectable(SchemaIndex index, string model) =>
    [
        .. index.AllMembers(model)
            .Select(_ => _.Member)
            .Where(_ => _ is {IsCollection: false, IsAttachment: false})
    ];

    /// <summary>
    /// Reads the snippet, makes the change, and writes the result: null when the snippet cannot be
    /// read, when the change declines, or when it leaves the text as it was.
    /// </summary>
    public static string? Edit(SchemaIndex index, string snippet, Func<BuilderQuery, BuilderQuery?> change)
    {
        if (Read(index, snippet).Query is not { } query ||
            change(query) is not { } changed)
        {
            return null;
        }

        var written = Write(index, changed);
        if (written == snippet)
        {
            return null;
        }

        return written;
    }

    /// <summary>
    /// Projects the member a path names, or stops projecting it. A member read through a navigation
    /// goes into that navigation's nested object, made when it is not there and taken out with its last
    /// column — an empty <c>new { }</c> is not a projection the server accepts — and the <c>Select</c>
    /// goes with its last column, leaving the server's default projection. Null for a path more than
    /// one navigation deep: see <see cref="ToggleNested"/>.
    /// </summary>
    public static BuilderQuery? ToggleColumn(BuilderQuery query, IReadOnlyList<string> path)
    {
        if (path.Count is 0 or > 2)
        {
            return null;
        }

        var columns = ToggleColumn(query.Columns ?? [], path, 0);
        return query with
        {
            Columns = columns.Count == 0 ? null : columns,
            Projection = null
        };
    }

    static List<BuilderColumn> ToggleColumn(IReadOnlyList<BuilderColumn> columns, IReadOnlyList<string> path, int depth)
    {
        var name = path[depth];
        var list = columns.ToList();
        if (depth == path.Count - 1)
        {
            var existing = list.FindIndex(_ => _ is MemberColumn member && member.Member == name);
            if (existing >= 0)
            {
                list.RemoveAt(existing);
                return list;
            }

            // A row's own columns ahead of the objects nested in it, as a starter query lays them out:
            // burying them between two nested objects makes the shorter half the harder to read.
            var firstNested = list.FindIndex(_ => _ is NestedColumn);
            list.Insert(firstNested < 0 ? list.Count : firstNested, new MemberColumn(name));
            return list;
        }

        var position = list.FindIndex(_ => _ is NestedColumn group && group.Member == name);
        if (position < 0)
        {
            list.Add(new NestedColumn(name, ToggleColumn([], path, depth + 1)));
            return list;
        }

        var nested = (NestedColumn) list[position];
        var inner = ToggleColumn(nested.Columns, path, depth + 1);
        if (inner.Count == 0)
        {
            list.RemoveAt(position);
        }
        else
        {
            list[position] = nested with
            {
                Columns = inner
            };
        }

        return list;
    }

    /// <summary>
    /// Projects into the navigation a path names — a nested object carrying the scalars a starter query
    /// would pick from its model — or takes that nested object out. Null where the navigation's model
    /// has nothing worth carrying, which would be an empty object.
    /// </summary>
    /// <remarks>
    /// One level only: a nested object's members are read as values, and the translator has no value
    /// for an object built inside one — <c>Owner = new { Manager = new { … } }</c> is refused when the
    /// query runs. So the builder offers to nest a navigation of the row, and no deeper.
    /// </remarks>
    public static BuilderQuery? ToggleNested(SchemaIndex index, BuilderQuery query, IReadOnlyList<string> path)
    {
        if (path.Count != 1 ||
            Model(index, query) is not { } model ||
            Member(index, model, path) is not {IsNavigation: true} navigation ||
            Target(index, navigation) is not { } target)
        {
            return null;
        }

        List<BuilderColumn> defaults =
        [
            .. index.AllMembers(target)
                .Select(_ => _.Member)
                .Where(_ => !_.IsNavigation && SchemaIndex.Suggestable(_))
                .Select(_ => new MemberColumn(_.Name))
        ];

        var present = Nested(query.Columns ?? [], path, 0) is not null;
        if (!present &&
            defaults.Count == 0)
        {
            return null;
        }

        var columns = ToggleNested(query.Columns ?? [], path, 0, defaults);
        return query with
        {
            Columns = columns.Count == 0 ? null : columns,
            Projection = null
        };
    }

    /// <summary>
    /// Takes a column kept as code out of the projection: out of the named navigation's nested object,
    /// or out of the top level when no group is named. The nested object goes with its last column, as
    /// the <c>Select</c> goes with the last of its own. Null where there is no such column.
    /// </summary>
    public static BuilderQuery? RemoveCodeColumn(BuilderQuery query, string? group, string code)
    {
        var columns = (query.Columns ?? []).ToList();
        if (group is null)
        {
            var position = columns.FindIndex(_ => _ is CodeColumn column && column.Code == code);
            if (position < 0)
            {
                return null;
            }

            columns.RemoveAt(position);
        }
        else
        {
            var position = columns.FindIndex(_ => _ is NestedColumn nested && nested.Member == group);
            if (position < 0)
            {
                return null;
            }

            var owner = (NestedColumn) columns[position];
            var inner = owner.Columns.ToList();
            var found = inner.FindIndex(_ => _ is CodeColumn column && column.Code == code);
            if (found < 0)
            {
                return null;
            }

            inner.RemoveAt(found);
            if (inner.Count == 0)
            {
                columns.RemoveAt(position);
            }
            else
            {
                columns[position] = owner with
                {
                    Columns = inner
                };
            }
        }

        return query with
        {
            Columns = columns.Count == 0 ? null : columns
        };
    }

    static NestedColumn? Nested(IReadOnlyList<BuilderColumn> columns, IReadOnlyList<string> path, int depth)
    {
        var nested = columns
            .OfType<NestedColumn>()
            .FirstOrDefault(_ => _.Member == path[depth]);

        if (nested is null ||
            depth == path.Count - 1)
        {
            return nested;
        }

        return Nested(nested.Columns, path, depth + 1);
    }

    static List<BuilderColumn> ToggleNested(IReadOnlyList<BuilderColumn> columns, IReadOnlyList<string> path, int depth, IReadOnlyList<BuilderColumn> defaults)
    {
        var name = path[depth];
        var list = columns.ToList();
        var position = list.FindIndex(_ => _ is NestedColumn group && group.Member == name);
        if (depth == path.Count - 1)
        {
            if (position >= 0)
            {
                list.RemoveAt(position);
            }
            else
            {
                list.Add(new NestedColumn(name, defaults));
            }

            return list;
        }

        if (position < 0)
        {
            list.Add(new NestedColumn(name, ToggleNested([], path, depth + 1, defaults)));
            return list;
        }

        var nested = (NestedColumn) list[position];
        var inner = ToggleNested(nested.Columns, path, depth + 1, defaults);
        if (inner.Count == 0)
        {
            list.RemoveAt(position);
        }
        else
        {
            list[position] = nested with
            {
                Columns = inner
            };
        }

        return list;
    }

    /// <summary>A condition on the member with its type's default operator and value.</summary>
    public static ComparisonFilter Filter(SchemaIndex index, BuilderMember member) =>
        new(member.Path, DefaultOperator(index, member.Member.TypeDisplay), DefaultValue(index, member.Member.TypeDisplay));

    /// <summary>
    /// Adds a condition on the first member a filter can name, at its type's default — for text a
    /// contains of nothing, which narrows nothing until a value is typed. Null where nothing on the
    /// model can be compared.
    /// </summary>
    public static BuilderQuery? AddFilter(SchemaIndex index, BuilderQuery query)
    {
        if (Model(index, query) is not { } model ||
            Comparable(index, model) is not [var first, ..])
        {
            return null;
        }

        return query with
        {
            Filters = [.. query.Filters, Filter(index, first)]
        };
    }

    /// <summary>
    /// Adds an ascending sort key on the first member a sort can name that the query does not already
    /// sort by. Null where every one of them is taken.
    /// </summary>
    public static BuilderQuery? AddOrder(SchemaIndex index, BuilderQuery query)
    {
        if (Model(index, query) is not { } model)
        {
            return null;
        }

        var used = query.Orders
            .Where(_ => _.Path is not null)
            .Select(_ => string.Join('.', _.Path!))
            .ToHashSet(StringComparer.Ordinal);

        if (Comparable(index, model).FirstOrDefault(_ => !used.Contains(_.Display)) is not { } next)
        {
            return null;
        }

        return query with
        {
            Orders = [.. query.Orders, new(next.Path, null, false)]
        };
    }

    /// <summary>
    /// Moves the query to another source, keeping what still compiles against its model — a condition
    /// on a member of the same type, a sort key or a column it has — and dropping the rest, along with
    /// anything kept as code, whose members the builder cannot vouch for.
    /// </summary>
    public static BuilderQuery? ChangeSource(SchemaIndex index, BuilderQuery query, string source)
    {
        if (Model(index, query) is not { } from ||
            index.Sources.FirstOrDefault(_ => _.Name == source)?.Model is not { } to)
        {
            return null;
        }

        var columns = Keep(index, to, [], query.Columns ?? []);
        return query with
        {
            Source = source,
            Filters =
            [
                .. query.Filters
                    .OfType<ComparisonFilter>()
                    .Where(_ => Member(index, to, _.Path)?.TypeDisplay is { } type &&
                                type == Member(index, from, _.Path)?.TypeDisplay)
            ],
            Orders = [.. query.Orders.Where(_ => _.Path is not null && Member(index, to, _.Path) is not null)],
            Columns = columns.Count == 0 ? null : columns,
            Projection = null
        };
    }

    static List<BuilderColumn> Keep(SchemaIndex index, string to, IReadOnlyList<string> prefix, IReadOnlyList<BuilderColumn> columns)
    {
        var kept = new List<BuilderColumn>();
        foreach (var column in columns)
        {
            switch (column)
            {
                case MemberColumn member when Member(index, to, [.. prefix, member.Member]) is {IsNavigation: false}:
                    kept.Add(member);
                    break;
                case NestedColumn nested when Member(index, to, [.. prefix, nested.Member]) is {IsNavigation: true}:
                    var inner = Keep(index, to, [.. prefix, nested.Member], nested.Columns);
                    if (inner.Count > 0)
                    {
                        kept.Add(nested with
                        {
                            Columns = inner
                        });
                    }

                    break;
            }
        }

        return kept;
    }
}
