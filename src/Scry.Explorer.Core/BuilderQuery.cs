namespace Scry;

/// <summary>
/// A query as the query builder edits it: the source, its conditions, its sort keys, its paging, its
/// columns, and how its result is taken. <see cref="QueryBuilder.Read"/> produces one from a snippet
/// and <see cref="QueryBuilder.Write"/> turns one back into the snippet's text.
/// </summary>
/// <remarks>
/// Whatever the builder cannot show as a row of its own — a condition joined with <c>||</c>, a column
/// computed from two members — is carried as the code it was written as (<see cref="CodeFilter"/>,
/// <see cref="CodeColumn"/>, <see cref="BuilderOrder.Code"/>) and written back unchanged. Editing the
/// rest of the query never costs the part the builder does not understand.
/// </remarks>
public sealed record BuilderQuery(string Source)
{
    /// <summary>The conditions of the query's <c>Where</c>, joined with <c>&amp;&amp;</c>.</summary>
    public IReadOnlyList<BuilderFilter> Filters { get; init; } = [];

    /// <summary>The sort keys: the first an <c>OrderBy</c>, the rest <c>ThenBy</c>.</summary>
    public IReadOnlyList<BuilderOrder> Orders { get; init; } = [];

    public int? Skip { get; init; }

    public int? Take { get; init; }

    /// <summary>
    /// The members of the <c>Select</c>'s anonymous object. Null for a query with no <c>Select</c>,
    /// which the server answers with every scalar member of the row.
    /// </summary>
    public IReadOnlyList<BuilderColumn>? Columns { get; init; }

    /// <summary>
    /// A <c>Select</c> whose body is not an anonymous object — <c>_ => _.Name</c> — kept as written.
    /// Set only where <see cref="Columns"/> is null.
    /// </summary>
    public string? Projection { get; init; }

    /// <summary>The terminal the query ends with — <c>CountAsync</c>, <c>FirstOrDefaultAsync</c> — or null for its rows.</summary>
    public string? Terminal { get; init; }

    /// <summary>The variable declarations ahead of the query, as written.</summary>
    public string Preamble { get; init; } = "";

    /// <summary>The name every lambda in the query gives its parameter.</summary>
    public string Parameter { get; init; } = "_";

    /// <summary>Whether the query was written after an <c>await</c>, which is kept.</summary>
    public bool Await { get; init; }

    /// <summary>Whether the query was written with a trailing semicolon, which is kept.</summary>
    public bool Semicolon { get; init; }
}

/// <summary>The comparisons a <see cref="ComparisonFilter"/> is written with.</summary>
public enum FilterOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Contains,
    StartsWith,
    EndsWith,
    IsTrue,
    IsFalse,
    IsNull,
    IsNotNull
}

/// <summary>One condition of the query's <c>Where</c>.</summary>
public abstract record BuilderFilter;

/// <summary>
/// A member compared against a value: <c>_.Department!.Name == "Sales"</c>, <c>_.Name.Contains("a")</c>,
/// <c>!_.Active</c>. The path runs through navigations to a scalar; the value is the C# the
/// comparison is written with, and null for an operator that takes none.
/// </summary>
public sealed record ComparisonFilter(IReadOnlyList<string> Path, FilterOperator Operator, string? Value) :
    BuilderFilter;

/// <summary>A condition the builder cannot show as a member, an operator and a value, kept as written.</summary>
public sealed record CodeFilter(string Code) :
    BuilderFilter;

/// <summary>
/// One sort key: a member path, or — for a key the builder cannot show as one — the code it was
/// written as. Exactly one of the two is set.
/// </summary>
public sealed record BuilderOrder(IReadOnlyList<string>? Path, string? Code, bool Descending);

/// <summary>One member of the <c>Select</c>'s anonymous object.</summary>
public abstract record BuilderColumn;

/// <summary>A scalar member projected under its own name: <c>_.Name</c>, or <c>_.Department!.Name</c> inside a nested object.</summary>
public sealed record MemberColumn(string Member) :
    BuilderColumn;

/// <summary>A navigation projected into: <c>Department = new { … }</c>, its columns read through it.</summary>
public sealed record NestedColumn(string Member, IReadOnlyList<BuilderColumn> Columns) :
    BuilderColumn;

/// <summary>A member of the projection the builder cannot show as a column, kept as written.</summary>
public sealed record CodeColumn(string Code) :
    BuilderColumn;

/// <summary>
/// What reading a snippet produced: the query, or why there is none. Both are null for a blank
/// snippet, which is nothing to read and nothing wrong.
/// </summary>
public sealed record BuilderRead(BuilderQuery? Query, string? Problem);

/// <summary>A member a filter or a sort key can name: its path from the source's model, and the member it ends at.</summary>
public sealed record BuilderMember(IReadOnlyList<string> Path, ScryMemberInfo Member)
{
    /// <summary>The path as the pane lists it: <c>Department.Name</c>.</summary>
    public string Display =>
        string.Join('.', Path);
}

/// <summary>How a value is entered for a member's type, and so how its text becomes C#.</summary>
public enum ValueKind
{
    /// <summary>A type no comparison is offered for: bulk bytes, an attachment, a navigation.</summary>
    None,
    Text,
    Character,
    Integer,
    Decimal,
    Double,
    Single,
    Boolean,
    Enum,
    Date,
    DateTime,
    DateTimeOffset,
    Time,
    Duration,
    Guid
}
