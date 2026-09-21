/// <summary>
/// Names the live query an HTTP request belongs to, so that a handler watching the wire can tell a
/// reconnect from a second live query asking the same thing.
/// </summary>
/// <remarks>
/// Carried in <see cref="HttpRequestMessage.Options"/>, which is a dictionary beside the request
/// rather than part of it: nothing here is serialized, sent, or visible to a server. The wire is a
/// compatibility contract and a diagnostic must not add to it.
/// </remarks>
static class LiveSessionStamp
{
    static HttpRequestOptionsKey<long> key = new("ScryLiveSession");

    public static void Write(HttpRequestMessage message, long session) =>
        message.Options.Set(key, session);

    public static long? Read(HttpRequestMessage message) =>
        message.Options.TryGetValue(key, out var session) ? session : null;
}
