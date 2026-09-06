/// <summary>
/// Every function over every scalar member, and every operator over every pair of them, in every
/// argument shape a client can send: the outcome is a translation or a rejection, and never any
/// other exception. The builder's catch arms are the only thing standing between a type a function
/// was not written for and a server fault, and each new function is a new gap — so the matrix is
/// generated from the schema and the enum rather than written case by case, and a function added
/// without a guard fails here naming the member it faulted on.
/// </summary>
/// <remarks>
/// Built and translated but never run: a fault at execution is the provider's — dividing by a
/// constant of the client's choosing, say — and answered as the fixed 500 by design. What this
/// refuses is a fault before the database is asked, which is a validator or builder gap.
/// </remarks>
[TestFixture]
public class FunctionMatrixTests
{
    [Test]
    public void EveryFunctionOverEveryMemberTranslatesOrIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var faults = new List<string>();
        foreach (var (source, member) in ScalarMembers())
        {
            foreach (var function in Enum.GetValues<KnownFunction>())
            {
                foreach (var arguments in ArgumentShapes())
                {
                    var call = new CallNode(function, new MemberNode([member]), arguments);
                    Probe(context, source, new SelectOp(new([new("x", new NodeValue(call))])), faults, $"{function}({source}.{member}, {Describe(arguments)})");
                }
            }
        }

        Assert.That(faults, Is.Empty, string.Join("\n", faults));
    }

    [Test]
    public void EveryOperatorOverEveryPairOfMembersTranslatesOrIsRejected()
    {
        using var context = TestContext.CreateSeeded();
        var faults = new List<string>();
        foreach (var group in ScalarMembers().GroupBy(_ => _.Source))
        {
            var members = group.Select(_ => _.Member).ToList();
            foreach (var left in members)
            {
                foreach (var right in members)
                {
                    foreach (var op in Enum.GetValues<BinaryOp>())
                    {
                        var node = new BinaryNode(op, new MemberNode([left]), new MemberNode([right]));
                        Probe(context, group.Key, new SelectOp(new([new("x", new NodeValue(node))])), faults, $"select {group.Key}.{left} {op} {right}");
                        Probe(context, group.Key, new WhereOp(node), faults, $"where {group.Key}.{left} {op} {right}");
                    }
                }

                foreach (var op in Enum.GetValues<UnaryOp>())
                {
                    var node = new UnaryNode(op, new MemberNode([left]));
                    Probe(context, group.Key, new SelectOp(new([new("x", new NodeValue(node))])), faults, $"select {op} {group.Key}.{left}");
                    Probe(context, group.Key, new WhereOp(node), faults, $"where {op} {group.Key}.{left}");
                }
            }
        }

        Assert.That(faults, Is.Empty, string.Join("\n", faults));
    }

    static void Probe(TestContext context, string root, QueryOp op, List<string> faults, string label)
    {
        try
        {
            SharedProcessor.Instance.ToQueryString(QueryRequest.Create(root, [op]), context, EmptyServiceProvider.Instance);
        }
        catch (ScryValidationException)
        {
            // The outcome a shape the function was not written for should have.
        }
        catch (Exception exception)
        {
            faults.Add($"{label}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // No arguments, then one and two of each kind a client can spell: the count and the type are
    // what the validator and the builder decide on, and a shape past the function's arity is a
    // rejection the matrix still has to see as one.
    static IEnumerable<IReadOnlyList<Node>> ArgumentShapes()
    {
        yield return [];
        foreach (var constant in new[] {new ConstNode("1", ClrTypeTag.Int32), new ConstNode("a", ClrTypeTag.String)})
        {
            yield return [constant];
            yield return [constant, constant];
        }
    }

    static string Describe(IReadOnlyList<Node> arguments) =>
        arguments.Count == 0 ? "no args" : $"{arguments.Count} x {((ConstNode) arguments[0]).Tag}";

    // Every scalar member of every source the context maps, read off the schema so a member added to
    // the model is in the matrix without anyone adding it here. A POCO has no SQL to ask for, and a
    // type opted in without a mapping (the test model carries two, to pin classification) faults on
    // its missing DbSet before any function is reached — see todo F20.
    static IEnumerable<(string Source, string Member)> ScalarMembers()
    {
        using var context = TestContext.CreateSeeded();
        var options = new ScryOptions(typeof(TestContext));
        options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        var schema = Schema.Build(options);
        foreach (var source in schema.Sources)
        {
            if (source.Kind == Scry.SourceKind.Poco ||
                context.Model.FindEntityType(source.ClrType) is null ||
                !schema.TryGetType(source.ClrType, out var meta))
            {
                continue;
            }

            foreach (var member in meta.Members.Values)
            {
                if (member.Kind == MemberKind.Scalar)
                {
                    yield return (source.Name, member.Name);
                }
            }
        }
    }
}
