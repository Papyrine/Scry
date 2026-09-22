namespace Scry;

/// <summary>
/// Handles a command in-process: registered in the container, and run on a service scope of its own
/// once the command has been bound, authorized and accepted. Its outcome is the command's: returning is
/// completing, throwing is failing.
/// </summary>
/// <remarks>
/// What it saves through <see cref="ScryCommandContext.Db"/> is saved after it returns, where
/// <see cref="ScryCommandContext.SaveChanges"/> leaves that on — and reported to the live queries that
/// read it, where the context carries <c>ScryChangeInterceptor</c>. Throwing a
/// <see cref="ScryCommandException"/> fails the command with that message shown to the client; anything
/// else fails it with a fixed message, and the real one goes to the audit trail.
/// </remarks>
// begin-snippet: commandHandlerInterface
public interface ICommandHandler<in TCommand>
{
    Task Handle(TCommand command, ScryCommandContext context, Cancel cancel);
}

/// <summary>A handler for a command that answers with <typeparamref name="TResult"/>.</summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<TResult> Handle(TCommand command, ScryCommandContext context, Cancel cancel);
}
// end-snippet
