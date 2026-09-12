namespace Scry;

/// <summary>
/// What kind of failure a non-success response reports. One value per answer the endpoints give, so a
/// client branches on a closed set rather than on an HTTP status that has to carry two meanings — a
/// malformed query and an oversized body are both <c>400</c>-shaped, and only one of them is worth
/// retrying.
/// </summary>
/// <remarks>
/// Deliberately coarser than the messages behind it: a code says which of the endpoint's own answers
/// this is, never which member or which rule was involved. Nothing here is knowable to a caller that
/// could not already read it off the status and the fixed message, which is what lets a code ride on
/// the fixed <c>500</c> body as safely as on a rejection.
/// </remarks>
public enum ScryErrorCode
{
    /// <summary>
    /// No code was carried. A body from a proxy or other middleware rather than from the endpoint, or
    /// one from a server newer than this client. The status is all there is to go on.
    /// </summary>
    Unknown,

    /// <summary>
    /// The request could not be read as the wire format at all — malformed JSON, an unknown
    /// discriminator, a member the vocabulary does not name. Never attributed to a stale client: a
    /// request this broken carries no usable stamp to attribute it with.
    /// </summary>
    WireFormat,

    /// <summary>
    /// The request was read but refused by the allow-list or a resource limit. A generated client
    /// cannot produce one, so this is a hand-written request or a client older than its server — see
    /// <see cref="StaleClient"/> for the half that is attributable.
    /// </summary>
    Validation,

    /// <summary>
    /// The failure is attributed to the request's schema stamp differing from the server's — a client
    /// generated against an older model surface. Surfaces as <see cref="ScryStaleClientException"/>,
    /// so one catch covers every failure whose remedy is regenerating or reloading the client.
    /// </summary>
    /// <remarks>
    /// Reported in place of <see cref="Validation"/> or <see cref="ExecutionFailed"/> rather than
    /// alongside either: what the client does about it is the same whichever it would have been, and
    /// the status still says which it was.
    /// </remarks>
    StaleClient,

    /// <summary>
    /// A row policy denied the rows the query asked for. Surfaces as
    /// <see cref="ScryPermissionException"/>: retrying will not help, and only the caller knows what
    /// to tell a user.
    /// </summary>
    Forbidden,

    /// <summary>
    /// The body was not declared <c>application/json</c>, so it was refused before being read. A Scry
    /// client always declares it; a cross-site form cannot.
    /// </summary>
    UnsupportedMedia,

    /// <summary>
    /// The query passed validation and failed while executing. The message is the fixed text — nothing
    /// internal leaves the server — so this code is the whole of what is said about it.
    /// </summary>
    ExecutionFailed
}
