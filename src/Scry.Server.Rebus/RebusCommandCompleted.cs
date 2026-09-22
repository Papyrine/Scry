namespace Scry;

/// <summary>
/// A handler's word that a command a Scry server sent is done: the reply to it, sent to the server's
/// input queue, which finishes the command there.
/// </summary>
/// <remarks>
/// Plain values only, so the bus's serializer never has to understand a Scry type.
/// </remarks>
public sealed class RebusCommandCompleted
{
    /// <summary>The command's id, as the client gave it.</summary>
    public Guid Id { get; set; }

    /// <summary>Whether its handlers finished without throwing.</summary>
    public bool Succeeded { get; set; }

    /// <summary>What the handler answered with, as JSON, for a command that has a result.</summary>
    public string? Result { get; set; }

    /// <summary>Why it failed: only ever what a caller may read.</summary>
    public string? Error { get; set; }
}

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

        processor.CompleteCommand(message.Id, result);
        return Task.CompletedTask;
    }
}
