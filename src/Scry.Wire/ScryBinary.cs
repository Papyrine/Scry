namespace Scry;

/// <summary>
/// The multipart format a response travels in when it carries raw binary parts — values of members
/// marked <c>[BinaryTransfer]</c> on the server model. Every binary part precedes the JSON document
/// that references it, and a diverted value appears in that JSON as <c>{"$bin":n}</c> where
/// <c>n</c> indexes the parts belonging to that document, 0-based in emission order. A null binary
/// value stays inline as JSON <c>null</c> and produces no part.
/// </summary>
/// <remarks>
/// For a single response and a batch the referencing document is the final JSON part and indices span
/// the whole response — a batch numbers its parts globally across entries. For a stream the
/// referencing document is each row line: <see cref="ScryStream.ContentType"/> sections holding
/// complete lines alternate with binary sections carrying the next row's parts, and indices reset
/// after every line, so a reader holds at most one row's parts.
/// </remarks>
// begin-snippet: wireBinary
public static class ScryBinary
{
    /// <summary>The media type a binary-carrying response is served as.</summary>
    public const string ContentType = "multipart/mixed";

    /// <summary>The media type of each raw binary part.</summary>
    public const string PartContentType = "application/octet-stream";

    /// <summary>
    /// The property a diverted binary value is replaced with in JSON. Projected member names come from
    /// the client's own C# identifiers, and <c>$</c> cannot start one, so no row can collide with it.
    /// </summary>
    public const string PartProperty = "$bin";

    /// <summary>
    /// The prefix of the multipart boundary. The rest is random per
    /// response, so part content is never scanned for collisions.
    /// </summary>
    public const string BoundaryPrefix = "scry";
}
// end-snippet
