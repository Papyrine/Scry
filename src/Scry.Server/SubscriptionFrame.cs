/// <summary>
/// One answer of a live query, as the bytes of a complete response and a name for them. The bytes are
/// valid until the next answer is asked for, which is long enough to write them out and no longer.
/// </summary>
/// <param name="Id">
/// A fingerprint of <paramref name="Json"/>. What one answer is told from the next by — and what a
/// reconnecting client sends back, so that a first answer it already holds is not sent again.
/// </param>
/// <param name="Json">The response, byte for byte what the query endpoint would have answered.</param>
readonly record struct SubscriptionFrame(string Id, ReadOnlyMemory<byte> Json);
