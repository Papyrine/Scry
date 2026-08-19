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
    public static Exception Read(int status, byte[] body)
    {
        var error = ScryJson.TryDeserializeError(body);
        if (error is {StaleClient: true, Error.Length: > 0})
        {
            return new ScryStaleClientException(error.Error);
        }

        if (status == 403 &&
            error is {Error.Length: > 0})
        {
            return new ScryPermissionException(error.Error);
        }

        return new ScryRequestException(status, Encoding.UTF8.GetString(body));
    }
}
