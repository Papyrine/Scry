/// <summary>
/// How closure state reaches a request. A captured value is read where the shape allows rather than
/// compiled into a delegate per send, and read once: a set tested for membership was evaluated twice,
/// once to ask whether it was another source and again for its values.
/// </summary>
public class ClosureEvaluationTests
{
    [Test]
    public async Task ACapturedSetIsReadOnce()
    {
        var holder = new Counting();

        var request = Client().Source<Order>("Order", ["Region"])
            .Where(_ => holder.Ids.Contains(_.Id))
            .ToScryRequest();

        var call = (CallNode) ((WhereOp) request.Pipeline[0]).Predicate;
        using (Assert.Multiple())
        {
            await Assert.That(call.Arguments).Count().IsEqualTo(2);
            await Assert.That(holder.Reads).IsEqualTo(1);
        }
    }

    // A chain of member reads rooted at a captured object, a boxing conversion over it, and a paging
    // count all read through without compiling — and read the same values compiling would.
    [Test]
    public async Task CapturedValuesReadThrough()
    {
        var holder = new Counting();
        var take = 3;

        var request = Client()
            .Source<Order>("Order", ["Region"])
            .Where(_ => _.Region == holder.Inner.Name && _.Amount > holder.Inner.Threshold)
            .Skip(holder.Inner.Offset)
            .Take(take)
            .ToScryRequest();

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        using (Assert.Multiple())
        {
            await Assert.That(((ConstNode) ((BinaryNode) predicate.Left).Right).Value).IsEqualTo("North");
            await Assert.That(((ConstNode) ((BinaryNode) predicate.Right).Right).Value).IsEqualTo("10.5");
            await Assert.That(((SkipOp) request.Pipeline[1]).Count).IsEqualTo(2);
            await Assert.That(((TakeOp) request.Pipeline[2]).Count).IsEqualTo(3);
        }
    }

    sealed class Counting
    {
        public int Reads { get; private set; }

        public List<int> Ids
        {
            get
            {
                Reads++;
                return [1, 2];
            }
        }

        public Bounds Inner { get; } = new();
    }

    sealed class Bounds
    {
        public string Name { get; } = "North";

        public decimal Threshold { get; } = 10.5m;

        public int Offset { get; } = 2;
    }

    static ScryClient Client() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));
}
