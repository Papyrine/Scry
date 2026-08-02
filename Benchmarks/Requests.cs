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

    static SelectOp Select(params string[] members) =>
        new(new([..members.Select(_ => new ProjectionMember(_, new NodeValue(new MemberNode([_]))))]));
}
