namespace Scry;

// begin-snippet: wireVersion
/// <summary>Wire format version constants.</summary>
public static class WireFormat
{
    /// <summary>The current wire format version.</summary>
    public const int Version = 2;

    /// <summary>
    /// The HTTP response header carrying the server's schema stamp. A successful response also carries
    /// it in the body (<see cref="QueryResponse.Stamp"/>, the channel every non-HTTP transport uses);
    /// the header additionally covers error responses, where there is no body to read it from. Part of
    /// the wire contract.
    /// </summary>
    public const string SchemaStampHeader = "Scry-Schema-Stamp";
    // end-snippet

    /// <summary>
    /// The version a pipeline actually needs. A request is stamped with the lowest version that can
    /// carry it whole, so a query using nothing new keeps working against an older server — while one
    /// carrying a shape that server would misread by ignoring — a side pipeline on a join or set
    /// operation, or an aggregate's filter or Distinct — is rejected outright rather than answered
    /// partially.
    /// </summary>
    public static int RequiredVersion(IReadOnlyList<QueryOp> pipeline)
    {
        foreach (var op in pipeline)
        {
            var richer = op switch
            {
                JoinOp join => join.InnerOps is not null ||
                               Richer(join.OuterKey) ||
                               Richer(join.InnerKey) ||
                               Richer(join.InnerPredicate) ||
                               join.Result.Any(_ => Richer(_.Aggregate)),
                SetOp set => set.OperandOps is not null ||
                             Richer(set.Predicate) ||
                             Richer(set.Projection),
                WhereOp where => Richer(where.Predicate),
                OrderByOp orderBy => Richer(orderBy.Key),
                ThenByOp thenBy => Richer(thenBy.Key),
                SelectOp select => Richer(select.Projection),
                GroupByOp groupBy => groupBy.Keys.Any(Richer),
                AggregateOp aggregate => Richer(aggregate.Selector),
                CountOp count => Richer(count.Predicate),
                LongCountOp longCount => Richer(longCount.Predicate),
                AnyOp any => Richer(any.Predicate),
                AllOp all => Richer(all.Predicate),
                FirstOp first => Richer(first.Predicate),
                SingleOp single => Richer(single.Predicate),
                LastOp last => Richer(last.Predicate),
                _ => false
            };

            if (richer)
            {
                return 2;
            }
        }

        return 1;
    }

    // Whether a node carries a version-2 shape anywhere in it. New node kinds default to false: a
    // whole new kind already fails an older server's deserialization by its discriminator, so only
    // new FIELDS on existing kinds need naming here.
    static bool Richer(Node? node) =>
        node switch
        {
            AggregateNode aggregate => aggregate.Predicate is not null ||
                                       aggregate.Distinct ||
                                       Richer(aggregate.Selector),
            BinaryNode binary => Richer(binary.Left) || Richer(binary.Right),
            UnaryNode unary => Richer(unary.Operand),
            ConditionalNode conditional => Richer(conditional.Test) ||
                                           Richer(conditional.IfTrue) ||
                                           Richer(conditional.IfFalse),
            CallNode call => Richer(call.Target) || call.Arguments.Any(Richer),
            CollateNode collate => Richer(collate.Target),
            SubqueryNode subquery => Richer(subquery.Predicate) || Richer(subquery.Selector),
            InSourceNode inSource => Richer(inSource.Value) ||
                                     Richer(inSource.Selector) ||
                                     Richer(inSource.Predicate),
            CompositeKeyNode composite => composite.Parts.Any(Richer),
            _ => false
        };

    static bool Richer(Projection? projection) =>
        projection is not null &&
        projection.Members.Any(_ => _.Value switch
        {
            NodeValue value => Richer(value.Node),
            NestedValue nested => Richer(nested.Projection),
            _ => false
        });

}
