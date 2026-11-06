namespace Scry;

/// <summary>Finishes a command on the server when the reply to it arrives.</summary>
/// <remarks>
/// Public because the container constructs it; register it as a Rebus handler on the server — with
/// Rebus.ServiceProvider, <c>services.AddRebusHandler&lt;RebusCommandCompletedHandler&gt;()</c>. A
/// completion for a command this node does not hold in flight changes nothing.
/// </remarks>
public sealed class RebusCommandCompletedHandler(ScryProcessor processor) :
    IHandleMessages<RebusCommandCompleted>
{
    /// <inheritdoc />
    public Task Handle(RebusCommandCompleted message)
    {
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
