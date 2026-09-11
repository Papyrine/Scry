/// <summary>
/// How a failure the server raised becomes the <see cref="ScryErrorCode"/> it is reported as. Shared
/// by the HTTP endpoints and the batch, which answer the same failures in different envelopes and
/// must code them identically — a rejected entry says exactly what the same query sent alone would.
/// </summary>
static class ErrorCodes
{
    /// <summary>
    /// A stale client's rejection is reported as that rather than as the ordinary validation failure
    /// it also is: what the client does about it — regenerate, or reload — is the same whichever rule
    /// it tripped, and the status still says it was a rejection.
    /// </summary>
    public static ScryErrorCode Classify(ScryValidationException exception) =>
        exception.StaleClient
            ? ScryErrorCode.StaleClient
            : ScryErrorCode.Validation;

    /// <summary>
    /// The same attribution for an execution failure, where there is no rejection to classify: a
    /// drifted client faulting the server is far more likely stale than the server broken, and saying
    /// so is what lets the client prompt a reload rather than present an unexplained server error.
    /// </summary>
    public static ScryErrorCode Failed(bool drifted) =>
        drifted
            ? ScryErrorCode.StaleClient
            : ScryErrorCode.ExecutionFailed;
}
