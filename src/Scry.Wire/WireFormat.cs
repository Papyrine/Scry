namespace Scry;

// begin-snippet: wireVersion
/// <summary>Wire format version constants.</summary>
public static class WireFormat
{
    /// <summary>The current wire format version.</summary>
    public const int Version = 2;

    /// <summary>
    /// The version a pipeline actually needs. A request is stamped with the lowest version that can
    /// carry it whole, so a query using nothing new keeps working against an older server — while one
    /// carrying a shape that server would misread by ignoring, a side pipeline on a join or set
    /// operation, is rejected outright rather than answered partially.
    /// </summary>
    public static int RequiredVersion(IReadOnlyList<QueryOp> pipeline)
    {
        foreach (var op in pipeline)
        {
            if (op is JoinOp {InnerOps: not null} or SetOp {OperandOps: not null})
            {
                return 2;
            }
        }

        return 1;
    }

    /// <summary>
    /// The HTTP response header carrying the server's schema stamp. A successful response also carries
    /// it in the body (<see cref="QueryResponse.Stamp"/>, the channel every non-HTTP transport uses);
    /// the header additionally covers error responses, where there is no body to read it from. Part of
    /// the wire contract.
    /// </summary>
    public const string SchemaStampHeader = "Scry-Schema-Stamp";
}
// end-snippet
