/// <summary>
/// Answers a command whose message went to the error queue — every attempt spent — as failed, to the
/// server waiting for it, which would otherwise hold it pending until it gave up on it.
/// </summary>
sealed class FailedCommands
{
    IMessageSession? session;

    public void Use(IMessageSession? current) =>
        session = current;

    public Task Answer(FailedMessage failed, Cancel cancel)
    {
        if (session is null ||
            !failed.Headers.TryGetValue(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header, out var id) ||
            !failed.Headers.TryGetValue(Headers.ReplyToAddress, out var replyTo))
        {
            return Task.CompletedTask;
        }

        var options = new SendOptions();
        options.SetDestination(replyTo);

        // The fixed text: what failed is in the error queue, with the exception that failed it, and a
        // caller is owed the fact rather than the stack.
        return session.Send(
            new ScryCommandCompleted
            {
                Id = id,
                Succeeded = false,
                Error = "Command execution failed."
            },
            options,
            cancel);
    }
}
