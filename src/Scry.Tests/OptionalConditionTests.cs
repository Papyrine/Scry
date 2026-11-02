/// <summary>
/// A <c>bool?</c> read as a condition. The client sends <c>Where(_ =&gt; _.Verified.Value)</c> as the
/// bare member — <c>.Value</c> is dropped as every unwrapping of an optional is — and the server once
/// refused it as no condition. It reads as true where it is true now, in a predicate and wherever else
/// a condition is read: under a negation, on either side of a conjunction, as a conditional's test.
/// The shared model has no such member, so the source is compiled and served in memory, where a null
/// has to read as false rather than fault on the unwrap.
/// </summary>
public class OptionalConditionTests
{
    const string model =
        """
        [QueryablePoco]
        public class Claim
        {
            public string Name { get; set; } = "";
            public bool? Verified { get; set; }
            public bool Open { get; set; }

            public static IEnumerable<Claim> Rows() =>
            [
                new Claim { Name = "Checked", Verified = true, Open = true },
                new Claim { Name = "Rejected", Verified = false, Open = true },
                new Claim { Name = "Pending", Verified = null, Open = false }
            ];
        }
        """;

    static MemberNode verified = new(["Verified"]);
    static MemberNode open = new(["Open"]);

    [Test]
    public async Task ReadsAsTrueWhereTrue() =>
        await Assert.That(Count(verified)).IsEqualTo(1);

    // A null is not true, so its negation holds: the pending claim counts beside the rejected one.
    [Test]
    public async Task NegatedReadsAsTrueWhereNotTrue() =>
        await Assert.That(Count(new UnaryNode(UnaryOp.Not, verified))).IsEqualTo(2);

    [Test]
    public async Task CombinesWithACondition()
    {
        var both = Count(new BinaryNode(BinaryOp.AndAlso, verified, open));
        var either = Count(new BinaryNode(BinaryOp.OrElse, verified, new UnaryNode(UnaryOp.Not, open)));

        using (Assert.Multiple())
        {
            await Assert.That(both).IsEqualTo(1).Because("only the checked claim is verified and open");
            await Assert.That(either).IsEqualTo(2).Because("the checked claim is verified and the pending one is closed");
        }
    }

    [Test]
    public async Task TestsAConditional()
    {
        var (processor, data) = CompiledPoco.Start(model, "Claim");
        await using var _ = data;
        var request = QueryRequest.Create(
            "Claim",
            [
                new SelectOp(new(
                [
                    new("Name", new NodeValue(new MemberNode(["Name"]))),
                    new("Verdict", new NodeValue(new ConditionalNode(verified, new ConstNode("yes", ClrTypeTag.String), new ConstNode("no", ClrTypeTag.String))))
                ]))
            ]);

        var response = processor.Execute(request, data);

        await Assert.That(response.Payload.EnumerateArray().Select(_ => _.GetProperty("verdict").GetString()!)).IsEquivalentTo(["yes", "no", "no"], CollectionOrdering.Matching);
    }

    static int Count(Node predicate)
    {
        var (processor, data) = CompiledPoco.Start(model, "Claim");
        using var _ = data;
        return processor.Execute(QueryRequest.Create("Claim", [new WhereOp(predicate), new CountOp()]), data).Payload.GetInt32();
    }
}
