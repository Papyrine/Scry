/// <summary>
/// Sends a <see cref="RebusCommandCompleted"/> to the address a command came from, inside the
/// transaction of the message being handled — so it leaves when, and only if, that transaction does.
/// </summary>
/// <remarks>
/// Straight through the transport, since a pipeline step has no bus of its own to reply with. Rebus's
/// outgoing steps would add the headers it wants; what the reader on the far side needs of them is the
/// id and the type, which are set here.
/// </remarks>
sealed class CompletionSender(ISerializer serializer, ITransport transport, IMessageTypeNameConvention typeNames)
{
    public static bool TryRead(IDictionary<string, string> headers, out Guid id, out string replyTo)
    {
        replyTo = "";
        if (!headers.TryGetValue(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header, out id))
        {
            id = default;
            return false;
        }

        if (!headers.TryGetValue(Headers.ReturnAddress, out var address))
        {
            return false;
        }

        replyTo = address;
        return true;
    }

    public async Task Send(string replyTo, RebusCommandCompleted completion, ITransactionContext transaction)
    {
        var headers = new Dictionary<string, string>
        {
            [Headers.MessageId] = Guid.NewGuid().ToString(),
            [Headers.Type] = typeNames.GetTypeName(typeof(RebusCommandCompleted)),
            [Headers.Intent] = Headers.IntentOptions.PointToPoint
        };
        var message = await serializer.Serialize(new(headers, completion));
        await transport.Send(replyTo, message, transaction);
    }
}
