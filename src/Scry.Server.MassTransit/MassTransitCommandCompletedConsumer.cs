/// <summary>Finishes a command on the server when its consumer says how it ended.</summary>
/// <remarks>
/// A completion for a command this node does not hold in flight — another node's, or one already
/// finished — changes nothing.
/// </remarks>
sealed class MassTransitCommandCompletedConsumer(ScryProcessor processor) :
    IConsumer<MassTransitCommandCompleted>
{
    /// <inheritdoc />
    public Task Consume(ConsumeContext<MassTransitCommandCompleted> context)
    {
        var message = context.Message;
        if (!message.Succeeded)
        {
            processor.FailCommand(message.Id, message.Error ?? "Command execution failed.");
            return Task.CompletedTask;
        }

        JsonElement? result = null;
        if (message.Result is { } json)
        {
            using var document = JsonDocument.Parse(json);
            result = document.RootElement.Clone();
        }

        processor.CompleteCommand(message.Id, result, ScryDisclosureRow.Parse(message.Rows));
        return Task.CompletedTask;
    }
}
