namespace Scry;

/// <summary>What a captured exchange was, decided from the request's shape and path.</summary>
public enum ScrySidecarKind
{
    /// <summary>A single query — GET with the encoded request in the URL, or POST with a JSON body.</summary>
    Query,

    /// <summary>A batch of queries POSTed to the batch endpoint.</summary>
    Batch,

    /// <summary>A streamed query. Its rows are never buffered, so only headers are recorded.</summary>
    Stream,

    /// <summary>An attachment fetch. Its bytes are never buffered, so only headers are recorded.</summary>
    Attachment,

    /// <summary>
    /// A live query. Its response never ends, so buffering it would hold the first answer back for
    /// ever; the request and the response headers are recorded, and the answers are not.
    /// </summary>
    Subscription,

    /// <summary>Traffic on the same HttpClient that is not a Scry exchange. Metadata only.</summary>
    Other
}
