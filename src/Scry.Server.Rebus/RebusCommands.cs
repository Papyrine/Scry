/// <summary>
/// <see cref="ICommandDispatcher"/> over Rebus: sends the command the server bound, with its id and
/// caller as headers, routed by the bus's own routing.
/// </summary>
sealed class RebusDispatcher(IServiceProvider services, BusCommands claims) :
    ICommandDispatcher
{
    // Nothing by default: a Rebus message is any class, so nothing about one says it is a command
    // meant for the bus rather than for an in-process handler.
    public bool CanDispatch(Type command) =>
        claims.Claims(command);

    public Task Dispatch(CommandEnvelope envelope, Cancel cancel)
    {
        var headers = new Dictionary<string, string>
        {
            [ScryCommandHeaders.CommandId] = envelope.Id.ToString("D")
        };
        if (envelope.Caller is { } caller)
        {
            headers[ScryCommandHeaders.Caller] = caller;
        }

        return services
            .GetRequiredService<IBus>()
            .Send(envelope.Command, headers);
    }
}

/// <summary>
/// Sends a <see cref="RebusCommandCompleted"/> to the address a command came from, inside the
/// transaction of the message being handled — so it leaves when, and only if, that transaction does.
/// </summary>
/// <remarks>
/// Straight through the transport, since a pipeline step has no bus of its own to reply with. Rebus's
/// outgoing steps would add the headers it wants; what the reader on the far side needs of them is the
/// id and the type, which are set here.
/// </remarks>
sealed class CompletionSender(ISerializer serializer, ITransport transport, IMessageTypeNameConvention typeNames)
{
    public static bool TryRead(IDictionary<string, string> headers, out Guid id, out string replyTo)
    {
        replyTo = "";
        if (!headers.TryGetValue(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header, out id))
        {
            id = default;
            return false;
        }

        if (!headers.TryGetValue(Headers.ReturnAddress, out var address))
        {
            return false;
        }

        replyTo = address;
        return true;
    }

    public async Task Send(string replyTo, RebusCommandCompleted completion, ITransactionContext transaction)
    {
        var headers = new Dictionary<string, string>
        {
            [Headers.MessageId] = Guid.NewGuid().ToString(),
            [Headers.Type] = typeNames.GetTypeName(typeof(RebusCommandCompleted)),
            [Headers.Intent] = Headers.IntentOptions.PointToPoint
        };
        var message = await serializer.Serialize(new Message(headers, completion));
        await transport.Send(replyTo, message, transaction);
    }
}

/// <summary>
/// After each message's handlers have run, replies to a message a Scry server sent as a command with
/// how it ended. Placed after the step that dispatches to the handlers, so it runs only once they have
/// returned without throwing — a failure leaves the pipeline before reaching it.
/// </summary>
sealed class CompletionStep(CompletionSender sender) :
    IIncomingStep
{
    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        var message = context.Load<Message>();
        if (CompletionSender.TryRead(message.Headers, out var id, out var replyTo))
        {
            await sender.Send(
                replyTo,
                new RebusCommandCompleted
                {
                    Id = id,
                    Succeeded = true,
                    Result = context.Load<CommandResult>()?.Json
                },
                context.Load<ITransactionContext>());
        }

        await next();
    }
}

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
            new RebusCommandCompleted
            {
                Id = id,
                Succeeded = false,
                Error = "Command execution failed."
            },
            scope.TransactionContext);
        await scope.CompleteAsync();
    }
}

/// <summary>What a handler answered with, set through <c>SetScryResult</c>.</summary>
sealed class CommandResult
{
    public string? Json { get; set; }
}
