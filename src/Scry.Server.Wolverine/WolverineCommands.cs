/// <summary>
/// <see cref="ICommandDispatcher"/> over Wolverine: sends the command the server bound, with its id and
/// caller as headers, routed by the application's own routing.
/// </summary>
sealed class WolverineDispatcher(IServiceProvider services, BusCommands claims) :
    ICommandDispatcher
{
    // Nothing by default: a Wolverine message is any class, so nothing about one says it is a command
    // meant for the bus rather than for an in-process handler.
    public bool CanDispatch(Type command) =>
        claims.Claims(command);

    public async Task Dispatch(CommandEnvelope envelope, Cancel cancel)
    {
        var options = new DeliveryOptions().WithHeader(ScryCommandHeaders.CommandId, envelope.Id.ToString("D"));
        if (envelope.Caller is { } caller)
        {
            options = options.WithHeader(ScryCommandHeaders.Caller, caller);
        }

        // A bus of its own, as a request's would be: the one Wolverine registers is scoped.
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IMessageBus>()
            .SendAsync(envelope.Command, options);
    }
}

/// <summary>
/// Wolverine middleware, after each handler of a message a Scry server sent as a command: responds to
/// the sender with how it ended. Added to every handler chain but the completion's own by
/// <c>UseScryCommands</c>, and does nothing for a message that is not a command.
/// </summary>
/// <remarks>
/// After, not finally: Wolverine runs it only once the handler has returned, and its error policy
/// decides what a throw comes to — which is why a message that ends in the error queue is answered from
/// there, by <c>AndScryFailure</c>, and not from here.
/// </remarks>
public static class ScryCommandMiddleware
{
    // Beside the envelope rather than in its headers: what a handler returns cascades as messages, so
    // a result has to be handed over some other way, and one table entry per envelope in flight is it.
    static ConditionalWeakTable<Envelope, string> results = new();

    /// <summary>Responds with how the command ended, once its handler has returned.</summary>
    public static Task AfterAsync(Envelope envelope, IMessageContext context)
    {
        if (!TryRead(envelope, out var id))
        {
            return Task.CompletedTask;
        }

        results.TryGetValue(envelope, out var json);
        results.Remove(envelope);
        return Respond(
            context,
            envelope,
            new()
            {
                Id = id,
                Succeeded = true,
                Result = json
            });
    }

    internal static void SetResult(Envelope envelope, string json) =>
        results.AddOrUpdate(envelope, json);

    internal static async ValueTask Failed(IWolverineRuntime runtime, IEnvelopeLifecycle lifecycle, Exception exception)
    {
        var envelope = lifecycle.Envelope;
        if (!TryRead(envelope, out var id))
        {
            return;
        }

        results.Remove(envelope);

        // The fixed text: what failed is in the error queue, with the exception that failed it, and a
        // caller is owed the fact rather than the stack.
        await lifecycle.RespondToSenderAsync(
            new WolverineCommandCompleted
            {
                Id = id,
                Succeeded = false,
                Error = "Command execution failed."
            });
        await lifecycle.FlushOutgoingMessagesAsync();
    }

    static bool TryRead([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Envelope? envelope, out Guid id)
    {
        id = default;
        return envelope is not null &&
               envelope.Headers.TryGetValue(ScryCommandHeaders.CommandId, out var header) &&
               Guid.TryParse(header, out id);
    }

    // Back to the node that sent it, where the transport says who that was; published where it does
    // not, for whatever the application routes completions to.
    static Task Respond(IMessageContext context, Envelope envelope, WolverineCommandCompleted completion)
    {
        if (envelope.ReplyUri is not null)
        {
            return context.RespondToSenderAsync(completion).AsTask();
        }

        return context.PublishAsync(completion).AsTask();
    }
}
