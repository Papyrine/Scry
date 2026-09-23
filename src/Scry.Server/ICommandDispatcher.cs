namespace Scry;

/// <summary>
/// Carries commands somewhere else to be handled — a message bus, a queue, another process — and reports
/// each one's outcome back through <see cref="ScryProcessor.CompleteCommand"/> or
/// <see cref="ScryProcessor.FailCommand"/>. Registered with <see cref="ScryOptions.AddDispatcher{TDispatcher}()"/>.
/// </summary>
/// <remarks>
/// A command reaches a dispatcher already bound into the server's own class, authorized, its target
/// read through its policies, and accepted: what is left is to deliver it. A dispatcher claims only what
/// it was told to, so adding one never silently takes a command from an in-process handler.
/// </remarks>
public interface ICommandDispatcher
{
    /// <summary>Whether this dispatcher carries commands of <paramref name="commandType"/>.</summary>
    bool CanDispatch(Type commandType);

    /// <summary>
    /// Hands the command on. Completes once it has been handed on — its outcome is reported later, and
    /// separately — and throws where it could not be, which fails the command.
    /// </summary>
    Task Dispatch(CommandEnvelope envelope, Cancel cancel);
}
