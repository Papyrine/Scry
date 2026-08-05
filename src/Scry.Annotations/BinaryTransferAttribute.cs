namespace Scry;

// begin-snippet: binaryTransfer
/// <summary>
/// Marks a <c>byte[]</c> member whose values travel as raw multipart parts in HTTP responses instead
/// of base64 strings inside the JSON payload. A server-side transfer-encoding concern only: the
/// member's queryable surface — generated client code, validation, introspection, and the schema
/// stamp — is exactly what it would be without the attribute. Applying it to a member that is not a
/// <c>byte[]</c>, or to one excluded from the queryable surface, fails at server startup.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class BinaryTransferAttribute :
    Attribute;
// end-snippet
