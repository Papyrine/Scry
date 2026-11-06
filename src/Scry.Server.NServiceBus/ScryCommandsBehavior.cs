/// <summary>
/// Around the handling of each incoming message a Scry server dispatched as a command: once its
/// handlers are done, replies with <see cref="ScryCommandCompleted"/> through the message's own
/// context. Registered by <c>UseScryCommands</c>.
/// </summary>
/// <remarks>
/// <para>
/// Through the context, and at the logical-message stage <c>UseScryChanges</c> publishes from, so the
/// reply leaves beside that publish: only once the handlers' work is kept — with the outbox, after its
/// transaction commits — and once for a message that was retried.
/// </para>
/// <para>
/// A handler that throws leaves through here without replying, and recoverability tries again. What
/// exhausts it is answered from the error-queue notification instead, since only that knows an attempt
/// was the last.
/// </para>
/// </remarks>
sealed class ScryCommandsBehavior :
    Behavior<IIncomingLogicalMessageContext>
{
    public override async Task Invoke(IIncomingLogicalMessageContext context, Func<Task> next)
    {
        if (!context.MessageHeaders.TryGetValue(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header, out var id))
        {
            await next();
            return;
        }

        var result = new CommandResult();
        context.Extensions.Set(result);
        await next();
        await context.Reply(
            new ScryCommandCompleted
            {
                Id = id,
                Succeeded = true,
                Result = result.Json,
                Rows = result.Rows
            });
    }
}
