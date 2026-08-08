/// <summary>
/// Source-generated <c>System.Text.Json</c> metadata for the wire vocabulary. The vocabulary is
/// closed — every operator, node, and envelope is named here or on a <c>[JsonDerivedType]</c> the
/// generator reaches from one — so the whole of it can be emitted at compile time instead of being
/// reflected over at startup. <c>ScryJson</c> resolves it first and only falls back to reflection for
/// what cannot be known here.
/// </summary>
/// <remarks>
/// Only the roots are listed: the generator follows properties and <c>[JsonDerivedType]</c>
/// attributes, so a new operator or node is covered by adding it to the closed set rather than
/// here. <c>WireMetadataTests</c> asserts that, so a type that does slip out of the generated set is
/// caught rather than quietly falling back.
/// </remarks>
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(AttachmentRequest))]
[JsonSerializable(typeof(QueryBatchRequest))]
[JsonSerializable(typeof(QueryBatchResponse))]
[JsonSerializable(typeof(ScryIntrospection))]
[JsonSerializable(typeof(ScryStreamMarker))]
[JsonSerializable(typeof(ScryError))]
sealed partial class WireJsonContext :
    JsonSerializerContext;
