namespace Scry.Wire;

// begin-snippet: wireVersion
/// <summary>Wire format version constants.</summary>
public static class WireFormat
{
    /// <summary>The current wire format version.</summary>
    public const int Version = 1;

    /// <summary>
    /// The HTTP response header carrying the server's schema stamp. A successful response also carries
    /// it in the body (<see cref="QueryResponse.Stamp"/>, the channel every non-HTTP transport uses);
    /// the header additionally covers error responses, where there is no body to read it from. Part of
    /// the wire contract.
    /// </summary>
    public const string SchemaStampHeader = "Scry-Schema-Stamp";
}
// end-snippet
