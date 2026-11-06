/// <summary>
/// Answers a command whose message went to the error queue — every delivery attempt spent — as failed,
/// to the server waiting for it, which would otherwise hold it pending until it gave up on it.
/// </summary>
sealed class CompletionErrorHandler(IErrorHandler inner, CompletionSender sender) :
    IErrorHandler
{
    public async Task HandlePoisonMessage(TransportMessage transportMessage, ITransactionContext transactionContext, ExceptionInfo exception)
    {
        await inner.HandlePoisonMessage(transportMessage, transactionContext, exception);
        if (!CompletionSender.TryRead(transportMessage.Headers, out var id, out var replyTo))
        {
            return;
        }

        // In a transaction of its own: the failed message's is rolled back, and what was sent inside it
        // with it. The fixed text: what failed is in the error queue, with the exception that failed it,
        // and a caller is owed the fact rather than the stack.
        using var scope = new RebusTransactionScope();
        await sender.Send(
            replyTo,
            new()
            {
                Id = id,
                Succeeded = false,
                Error = "Command execution failed."
            },
            scope.TransactionContext);
        await scope.CompleteAsync();
    }
}
