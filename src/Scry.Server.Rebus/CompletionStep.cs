/// <summary>
/// After each message's handlers have run, replies to a message a Scry server sent as a command with
/// how it ended. Placed after the step that dispatches to the handlers, so it runs only once they have
/// returned without throwing — a failure leaves the pipeline before reaching it.
/// </summary>
sealed class CompletionStep(CompletionSender sender) :
    IIncomingStep
{
    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        var message = context.Load<Message>();
        // A second-level delivery dispatches the message as IFailed<T> because the command's own
        // handlers threw: one returning is handling that failure — most often deferring the message
        // for a later attempt — not the command succeeding. That attempt, or the error queue, answers.
        if (!context.Load<bool>(DefaultRetryStep.DispatchAsFailedMessageKey) &&
            CompletionSender.TryRead(message.Headers, out var id, out var replyTo))
        {
            var result = context.Load<CommandResult>();
            await sender.Send(
                replyTo,
                new()
                {
                    Id = id,
                    Succeeded = true,
                    Result = result?.Json,
                    Rows = result?.Rows
                },
                context.Load<ITransactionContext>());
        }

        await next();
    }
}
