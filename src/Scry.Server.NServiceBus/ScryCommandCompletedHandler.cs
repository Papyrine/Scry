namespace Scry;

/// <summary>
/// Receives <see cref="ScryCommandCompleted"/> on the Scry server that dispatched the command, and
/// finishes the command there. Found by NServiceBus's assembly scanning like any other handler.
/// </summary>
/// <remarks>
/// Public because NServiceBus constructs it. It does nothing a host would call. A completion for a
/// command the server does not hold in flight — finished already, pruned, or redelivered — changes
/// nothing, since a command finishes once.
/// </remarks>
public sealed class ScryCommandCompletedHandler(IServiceProvider services) :
    IHandleMessages<ScryCommandCompleted>
{
    /// <inheritdoc />
    public Task Handle(ScryCommandCompleted message, IMessageHandlerContext context)
    {
        // Absent on an endpoint that scanned this assembly and serves no Scry: a worker.
        if (services.GetService<ScryProcessor>() is not { } processor)
        {
            return Task.CompletedTask;
        }

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

        processor.CompleteCommand(message.Id, result);
        return Task.CompletedTask;
    }
}
