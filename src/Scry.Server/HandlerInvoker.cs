/// <summary>
/// The typed call that runs a command's in-process handler, reached through a class closed over the
/// command (and its result) once rather than a <see cref="MethodInfo"/> invoked reflectively per
/// command.
/// </summary>
interface IHandlerInvoker
{
    /// <summary>The handler interface the container is asked for.</summary>
    Type HandlerType { get; }

    /// <summary>Runs the handler, answering with its result, or null for a command that has none.</summary>
    Task<object?> Invoke(object handler, object command, ScryCommandContext context, Cancel cancel);
}

static class HandlerInvoker
{
    static ConcurrentDictionary<Type, IHandlerInvoker> invokers = new();

    public static IHandlerInvoker For(CommandMeta meta) =>
        invokers.GetOrAdd(
            meta.ClrType,
            _ =>
            {
                if (meta.Result is { } result)
                {
                    return (IHandlerInvoker) Activator.CreateInstance(typeof(HandlerInvoker<,>).MakeGenericType(meta.ClrType, result))!;
                }

                return (IHandlerInvoker) Activator.CreateInstance(typeof(HandlerInvoker<>).MakeGenericType(meta.ClrType))!;
            });
}

sealed class HandlerInvoker<TCommand> :
    IHandlerInvoker
{
    public Type HandlerType => typeof(ICommandHandler<TCommand>);

    public async Task<object?> Invoke(object handler, object command, ScryCommandContext context, Cancel cancel)
    {
        await ((ICommandHandler<TCommand>) handler).Handle((TCommand) command, context, cancel);
        return null;
    }
}

sealed class HandlerInvoker<TCommand, TResult> :
    IHandlerInvoker
{
    public Type HandlerType => typeof(ICommandHandler<TCommand, TResult>);

    public async Task<object?> Invoke(object handler, object command, ScryCommandContext context, Cancel cancel) =>
        await ((ICommandHandler<TCommand, TResult>) handler).Handle((TCommand) command, context, cancel);
}
