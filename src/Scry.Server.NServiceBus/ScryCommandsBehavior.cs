/// <summary>
/// Around the handling of each incoming message a Scry server dispatched as a command: once its
/// handlers are done, replies with <see cref="ScryCommandCompleted"/> through the message's own
/// context. Registered by <c>UseScryCommands</c>.
/// </summary>
/// <remarks>
/// <para>
/// Through the context, and at the logical-message stage <c>UseScryChanges</c> publishes from, so the
/// reply leaves beside that publish: only once the handlers' work is kept — with the outbox, after its
/// transaction commits — and once for a message that was retried.
/// </para>
/// <para>
/// A handler that throws leaves through here without replying, and recoverability tries again. What
/// exhausts it is answered from the error-queue notification instead, since only that knows an attempt
/// was the last.
/// </para>
/// </remarks>
sealed class ScryCommandsBehavior :
    Behavior<IIncomingLogicalMessageContext>
{
    public override async Task Invoke(IIncomingLogicalMessageContext context, Func<Task> next)
    {
        if (!context.MessageHeaders.TryGetValue(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header, out var id))
        {
            await next();
            return;
        }

        var result = new CommandResult();
        context.Extensions.Set(result);
        await next();
        await context.Reply(
            new ScryCommandCompleted
            {
                Id = id,
                Succeeded = true,
                Result = result.Json
            });
    }
}

/// <summary>What a handler answered with, set through <c>SetScryResult</c>.</summary>
sealed class CommandResult
{
    public string? Json { get; set; }
}

/// <summary>
/// Answers a command whose message went to the error queue — every attempt spent — as failed, to the
/// server waiting for it, which would otherwise hold it pending until it gave up on it.
/// </summary>
sealed class FailedCommands
{
    IMessageSession? session;

    public void Use(IMessageSession? current) =>
        session = current;

    public Task Answer(FailedMessage failed, Cancel cancel)
    {
        if (session is null ||
            !failed.Headers.TryGetValue(ScryCommandHeaders.CommandId, out var header) ||
            !Guid.TryParse(header, out var id) ||
            !failed.Headers.TryGetValue(Headers.ReplyToAddress, out var replyTo))
        {
            return Task.CompletedTask;
        }

        var options = new SendOptions();
        options.SetDestination(replyTo);

        // The fixed text: what failed is in the error queue, with the exception that failed it, and a
        // caller is owed the fact rather than the stack.
        return session.Send(
            new ScryCommandCompleted
            {
                Id = id,
                Succeeded = false,
                Error = "Command execution failed."
            },
            options,
            cancel);
    }
}

/// <summary>Hands <see cref="FailedCommands"/> the endpoint's session once there is one.</summary>
sealed class ScryCommandsFeature :
    Feature
{
    protected override void Setup(FeatureConfigurationContext context) =>
        context.RegisterStartupTask(new SessionStartup(context.Settings.Get<FailedCommands>()));

    sealed class SessionStartup(FailedCommands failed) :
        FeatureStartupTask
    {
        protected override Task OnStart(IMessageSession session, Cancel cancel = default)
        {
            failed.Use(session);
            return Task.CompletedTask;
        }

        protected override Task OnStop(IMessageSession session, Cancel cancel = default)
        {
            failed.Use(null);
            return Task.CompletedTask;
        }
    }
}
