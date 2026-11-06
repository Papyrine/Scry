namespace Scry;

/// <summary>Finishes a command on the server when the response to it arrives.</summary>
/// <remarks>
/// Public because Wolverine discovers and calls it; <c>AddScryCommandCompletions</c> includes it. A
/// completion for a command this node does not hold in flight changes nothing.
/// </remarks>
public static class WolverineCommandCompletedHandler
{
    /// <summary>Handles the completion.</summary>
    public static void Handle(WolverineCommandCompleted message, ScryProcessor processor)
    {
        if (!message.Succeeded)
        {
            processor.FailCommand(message.Id, message.Error ?? "Command execution failed.");
            return;
        }

        JsonElement? result = null;
        if (message.Result is { } json)
        {
            using var document = JsonDocument.Parse(json);
            result = document.RootElement.Clone();
        }

        processor.CompleteCommand(message.Id, result, ScryDisclosureRow.Parse(message.Rows));
    }
}
