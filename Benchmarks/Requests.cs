using Scry;

namespace Benchmarks;

/// <summary>The query shapes under measurement, built as wire requests exactly as a client would send them.</summary>
public static class Requests
{
    /// <summary>Every scalar the source has, so row shaping and serialization dominate the measurement.</summary>
    public static QueryRequest Wide() =>
        QueryRequest.Create(
            "MemRow",
            [
                Select("Id", "Name", "Region", "Grade", "Active", "Amount", "Ticks", "Created", "Score")
            ]);

    /// <summary>
    /// The same projection bounded as a page. A page is rows like a list is, so it is written the same
    /// way; what it adds is the envelope around them.
    /// </summary>
    public static QueryRequest Page(int size) =>
        QueryRequest.Create(
            "MemRow",
            [
                Select("Id", "Name", "Region", "Grade", "Active", "Amount", "Ticks", "Created", "Score"),
                new PageOp(size)
            ]);

    /// <summary>
    /// The same projection folded to its first row. A terminal's cost is per request rather than per
    /// row, so this measures the constant a query pays however many rows the source holds.
    /// </summary>
    public static QueryRequest Single() =>
        QueryRequest.Create(
            "MemRow",
            [
                Select("Id", "Name", "Region", "Grade", "Active", "Amount", "Ticks", "Created", "Score"),
                new FirstOp(OrDefault: true, Predicate: null)
            ]);

    /// <summary>A count: the smallest result there is, and the whole of it is the envelope.</summary>
    public static QueryRequest Scalar() =>
        QueryRequest.Create("MemRow", [new CountOp()]);

    /// <summary>
    /// A predicate over two members and a projection of four: the plain path a preparation pays, and
    /// the baseline the other preparation arms are read against.
    /// </summary>
    public static QueryRequest Filtered() =>
        QueryRequest.Create(
            "Account",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.AndAlso,
                        new BinaryNode(BinaryOp.Equal, Member("Active"), new ConstNode("true", ClrTypeTag.Boolean)),
                        new BinaryNode(BinaryOp.GreaterThan, Member("Amount"), new ConstNode("10", ClrTypeTag.Decimal)))),
                Select("Id", "Name", "Region", "Amount")
            ]);

    /// <summary>
    /// Temporal reads in the predicate and the projection, one of them through a nullable: each is a
    /// member looked up by name on the temporal type as the query is built.
    /// </summary>
    public static QueryRequest Temporal() =>
        QueryRequest.Create(
            "Account",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        Call(KnownFunction.DateYear, "Created"),
                        new ConstNode("2026", ClrTypeTag.Int32))),
                new SelectOp(new([
                    new("Year", new NodeValue(Call(KnownFunction.DateYear, "Created"))),
                    new("Month", new NodeValue(Call(KnownFunction.DateMonth, "Created"))),
                    new("Day", new NodeValue(Call(KnownFunction.DateDay, "Created"))),
                    new("ClosedYear", new NodeValue(Call(KnownFunction.DateYear, "Closed")))
                ]))
            ]);

    /// <summary>A membership test over three constants: one collection parameter, built per request.</summary>
    public static QueryRequest InList() =>
        QueryRequest.Create(
            "Account",
            [
                new WhereOp(new CallNode(
                    KnownFunction.In,
                    Member("Region"),
                    [Text("North"), Text("South"), Text("East")])),
                Select("Id", "Name")
            ]);

    /// <summary>An inner join: a second source resolved beside the root, and a projection naming both sides.</summary>
    public static QueryRequest Joined() =>
        QueryRequest.Create(
            "Account",
            [
                new JoinOp(
                    "Territory",
                    JoinKind.Inner,
                    Member("TerritoryId"),
                    Member("Id"),
                    [new("Name", JoinSide.Outer, ["Name"]), new("Territory", JoinSide.Inner, ["Name"])])
            ]);

    /// <summary>A filtered shape over the policied source, so what the arm adds is the policy.</summary>
    public static QueryRequest Policied() =>
        QueryRequest.Create(
            "GuardedAccount",
            [
                new WhereOp(new BinaryNode(BinaryOp.GreaterThan, Member("Amount"), new ConstNode("10", ClrTypeTag.Decimal))),
                Select("Id", "Name")
            ]);

    /// <summary>
    /// A two-member projection deduplicated and then ordered: the projected pair becomes a row type
    /// closed over the two member types, whose constructor and members are looked up as it is built.
    /// </summary>
    public static QueryRequest DistinctComposite() =>
        QueryRequest.Create(
            "Account",
            [
                Select("Region", "Grade"),
                new DistinctOp(),
                new OrderByOp(Member("Region"), Descending: false)
            ]);

    static SelectOp Select(params string[] members) =>
        new(new([..members.Select(_ => new ProjectionMember(_, new NodeValue(new MemberNode([_]))))]));

    static MemberNode Member(string name) =>
        new([name]);

    static ConstNode Text(string value) =>
        new(value, ClrTypeTag.String);

    static CallNode Call(KnownFunction function, string member) =>
        new(function, Member(member), []);
}
