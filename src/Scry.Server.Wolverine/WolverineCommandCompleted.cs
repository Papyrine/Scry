namespace Scry;

/// <summary>
/// A handler's word that a command a Scry server sent is done: the response to it, sent back to the
/// node that sent the command, which finishes it there.
/// </summary>
/// <remarks>
/// Plain values only, so the bus's serializer never has to understand a Scry type.
/// </remarks>
public sealed class WolverineCommandCompleted
{
    /// <summary>The command's id, as the client gave it.</summary>
    public Guid Id { get; set; }

    /// <summary>Whether its handler finished without throwing.</summary>
    public bool Succeeded { get; set; }

    /// <summary>What the handler answered with, as JSON, for a command that has a result.</summary>
    public string? Result { get; set; }

    /// <summary>Why it failed: only ever what a caller may read.</summary>
    public string? Error { get; set; }
}

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

        processor.CompleteCommand(message.Id, result);
    }
}
