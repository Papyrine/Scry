/// <summary>
/// One thing a live query's transport said, short of failing: an answer, that the answer already held
/// is still the answer, or that the server has ended the stream. A failure is an exception instead,
/// and a heartbeat is the transport's own business and never gets this far.
/// </summary>
/// <param name="Response">The answer, or null for the two frames that carry none.</param>
/// <param name="Id">
/// The server's name for the answer, sent back when asking again so an answer already held is not
/// sent twice. Null from a transport that has none.
/// </param>
/// <param name="Ended">Whether the server ended the stream.</param>
/// <param name="Reconnect">On an ending, whether asking again is expected to work.</param>
readonly record struct LiveFrame(QueryResponse? Response, string? Id, bool Ended, bool Reconnect)
{
    public static LiveFrame Result(QueryResponse response, string? id) =>
        new(response, id, Ended: false, Reconnect: false);

    public static LiveFrame Unchanged { get; } =
        new(Response: null, Id: null, Ended: false, Reconnect: false);

    public static LiveFrame End(bool reconnect) =>
        new(Response: null, Id: null, Ended: true, reconnect);
}
