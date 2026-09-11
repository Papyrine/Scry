/// <summary>
/// What a response the server did not answer with a result surfaces as. One decision, made in one
/// place, because every transport this client speaks — query, stream, batch, attachment — has the same
/// three answers to give and code that catches one of them should not have to know which call produced
/// it.
/// </summary>
static class ResponseFailure
{
    /// <summary>
    /// The exception for a non-success status. A failure the server attributed to this client's schema
    /// stamp surfaces as the same exception the payload reader throws for an unknown enum value, so one
    /// catch covers every stale-client failure and can prompt a reload; a denial surfaces as its own
    /// type, since retrying it will not help and only the caller knows what to tell a user.
    /// </summary>
    public static Exception Read(HttpStatusCode status, byte[] body)
    {
        // Not one of ours, or from something in the way that answers in its own shape: there is no
        // code to dispatch on and the status is all the caller gets.
        if (ScryJson.TryDeserializeError(body) is not {Error.Length: > 0} error)
        {
            return new ScryRequestException(status, ScryErrorCode.Unknown, Encoding.UTF8.GetString(body));
        }

        return error.Code switch
        {
            ScryErrorCode.StaleClient => new ScryStaleClientException(error.Error),
            ScryErrorCode.Forbidden => new ScryPermissionException(error.Error),
            // Everything else keeps the raw body: the code says which answer this is, and the body
            // carries the message a person reads.
            _ => new ScryRequestException(status, error.Code, Encoding.UTF8.GetString(body))
        };
    }
}
