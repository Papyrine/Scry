/// <summary>
/// <see cref="ICommandDispatcher"/> over MassTransit: publishes the command the server bound, with its
/// id and caller as headers, to whichever consumer handles it.
/// </summary>
sealed class MassTransitDispatcher(IServiceProvider services, BusCommands claims) :
    ICommandDispatcher
{
    // Nothing by default: a MassTransit message is any class, so nothing about one says it is a
    // command meant for the bus rather than for an in-process handler.
    public bool CanDispatch(Type command) =>
        claims.Claims(command);

    public Task Dispatch(CommandEnvelope envelope, Cancel cancel) =>
        services
            .GetRequiredService<IBus>()
            .Publish(
                envelope.Command,
                envelope.CommandType,
                Pipe.Execute<PublishContext>(
                    context =>
                    {
                        context.Headers.Set(ScryCommandHeaders.CommandId, envelope.Id.ToString("D"));
                        if (envelope.Caller is { } caller)
                        {
                            context.Headers.Set(ScryCommandHeaders.Caller, caller);
                        }
                    }),
                cancel);
}

/// <summary>Finishes a command on the server when its consumer says how it ended.</summary>
/// <remarks>
/// Public because MassTransit constructs it. A completion for a command this node does not hold in
/// flight — another node's, or one already finished — changes nothing.
/// </remarks>
public sealed class MassTransitCommandCompletedConsumer(ScryProcessor processor) :
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

        processor.CompleteCommand(message.Id, result);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Around each message a Scry server published as a command: once its consumers are done, publishes
/// how it ended. Registered for every message type by <c>UseScryCommands</c>, and passes anything else
/// straight through.
/// </summary>
/// <remarks>
/// The success goes through the consume context, so with an outbox it leaves only if the consumer's
/// work is kept. The failure goes through the bus, since an outbox discards what a faulted consume
/// published — and it is reported when the failure leaves this filter, which is once retries are
/// spent where the filter is configured outside the retry.
/// </remarks>
sealed class ScryCommandFilter<TMessage>(IBus bus) :
    IFilter<ConsumeContext<TMessage>>
    where TMessage : class
{
    public async Task Send(ConsumeContext<TMessage> context, IPipe<ConsumeContext<TMessage>> next)
    {
        if (!context.Headers.TryGetHeader(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header?.ToString(), out var id))
        {
            await next.Send(context);
            return;
        }

        var result = context.GetOrAddPayload(() => new CommandResult());
        try
        {
            await next.Send(context);
        }
        catch (Exception)
        {
            // The fixed text: what failed is on the fault, with its exception, and a caller is owed
            // the fact rather than the stack.
            await bus.Publish(
                new MassTransitCommandCompleted
                {
                    Id = id,
                    Succeeded = false,
                    Error = "Command execution failed."
                });
            throw;
        }

        await context.Publish(
            new MassTransitCommandCompleted
            {
                Id = id,
                Succeeded = true,
                Result = result.Json
            });
    }

    public void Probe(ProbeContext context) =>
        context.CreateFilterScope("scryCommands");
}

/// <summary>What a consumer answered with, set through <c>SetScryResult</c>.</summary>
sealed class CommandResult
{
    public string? Json { get; set; }
}
