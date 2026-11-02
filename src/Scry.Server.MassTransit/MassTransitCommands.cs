/// <summary>
/// <see cref="ICommandDispatcher"/> over MassTransit: publishes the command the server bound, with its
/// id and caller as headers, to whichever consumer handles it. The message's own id is the command's,
/// which is what lets the fault MassTransit publishes for it name the command.
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
                        context.MessageId = envelope.Id;
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

        processor.CompleteCommand(message.Id, result);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fails a command on the server once MassTransit has given up on it: the fault it publishes when a
/// message's consume has failed for good — after every retry and redelivery, or at once where there
/// are none. Every fault carries the id of the message it is about, and a command's message carries
/// the command's id as its own.
/// </summary>
/// <remarks>
/// The non-generic <see cref="Fault"/>, which every <c>Fault&lt;T&gt;</c> is too, so that one consumer
/// hears the commands of every type — the types a server carries are its options', and may be all of
/// them. A fault of anything else names no command this node holds, and changes nothing.
/// </remarks>
sealed class MassTransitCommandFaultConsumer(ScryProcessor processor) :
    IConsumer<Fault>
{
    /// <inheritdoc />
    public Task Consume(ConsumeContext<Fault> context)
    {
        if (context.Message.FaultedMessageId is { } id)
        {
            // The fixed text: what failed is on the fault, with its exception, and a caller is owed
            // the fact rather than the stack.
            processor.FailCommand(id, "Command execution failed.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Around each message a Scry server published as a command: once its consumers are done, publishes
/// that it succeeded. Registered for every message type by <c>UseScryCommands</c>, and passes anything
/// else straight through.
/// </summary>
/// <remarks>
/// The success goes through the consume context, so with an outbox it leaves only if the consumer's
/// work is kept. A failure is not reported here: the filter runs inside a <c>UseMessageRetry</c>,
/// whatever order the two were configured in, and is told which attempt it is in but not whether
/// another will follow. A failed attempt is rethrown for the retry to decide, and the command is
/// failed by the fault MassTransit publishes once nothing more will be tried
/// (<see cref="MassTransitCommandFaultConsumer"/>).
/// </remarks>
sealed class ScryCommandFilter<TMessage> :
    IFilter<ConsumeContext<TMessage>>
    where TMessage : class
{
    public async Task Send(ConsumeContext<TMessage> context, IPipe<ConsumeContext<TMessage>> next)
    {
        if (!context.Headers.TryGetHeader(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header.ToString(), out var id))
        {
            await next.Send(context);
            return;
        }

        var result = context.GetOrAddPayload(() => new CommandResult());
        await next.Send(context);
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
