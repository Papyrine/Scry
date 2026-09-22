/// <summary>
/// What a command policy type declares about itself, and the typed calls used to ask it. Mirrors
/// <see cref="AttachmentPolicy"/>: <c>[Command(Policy = ...)]</c> takes a bare <see cref="Type"/> the
/// compiler cannot check, so the shape is read here and a wrong one refused at startup.
/// </summary>
static class CommandPolicy
{
    static ConcurrentDictionary<(Type Policy, Type Command), (Type? Rows, ICommandPolicyInvoker Invoker)> described = new();

    /// <summary>
    /// The type the policy's row interface filters, or null where it implements only the command-wide
    /// one. Throws where the policy does not implement <see cref="ICommandPolicy{TCommand}"/> for this
    /// command, or implements the row interface for more than one type.
    /// </summary>
    public static Type? RowsEntity(Type policy, Type command) =>
        Describe(policy, command).Rows;

    /// <summary>
    /// Whether the caller may send the command at all. The policy is resolved from the request's
    /// services where it was registered and constructed otherwise, exactly as a row policy is.
    /// </summary>
    public static bool Allow(Type policy, Type command, ScryPolicyContext context) =>
        Describe(policy, command).Invoker.Allow(Instance(policy, context.Services), context);

    /// <summary>
    /// The rows the caller may send the command against, as the policy's expression, or null where the
    /// policy has no row interface.
    /// </summary>
    public static LambdaExpression? Rows(Type policy, Type command, ScryPolicyContext context)
    {
        var invoker = Describe(policy, command).Invoker;
        if (!invoker.HasRows)
        {
            return null;
        }

        return invoker.Rows(Instance(policy, context.Services), context) ??
               throw new($"Command policy '{policy.Name}' answered Rows with null. A row policy answers with the rows the caller may send '{command.Name}' against — `_ => true` for all of them.");
    }

    static object Instance(Type policy, IServiceProvider services) =>
        services.GetService(policy) ??
        Activator.CreateInstance(policy) ??
        throw new($"Could not create command policy '{policy.Name}'.");

    static (Type? Rows, ICommandPolicyInvoker Invoker) Describe(Type policy, Type command) =>
        described.GetOrAdd(
            (policy, command),
            key =>
            {
                var (type, commandType) = key;
                if (!typeof(ICommandPolicy<>).MakeGenericType(commandType).IsAssignableFrom(type))
                {
                    throw new($"Command policy '{type.Name}' does not implement ICommandPolicy<{commandType.Name}>, so it decides nothing about '{commandType.Name}'.");
                }

                var rows = type.GetInterfaces()
                    .Where(_ => _.IsGenericType &&
                                _.GetGenericTypeDefinition() == typeof(ICommandPolicy<,>) &&
                                _.GenericTypeArguments[0] == commandType)
                    .Select(_ => _.GenericTypeArguments[1])
                    .ToList();
                if (rows.Count > 1)
                {
                    throw new(
                        $"Command policy '{type.Name}' implements ICommandPolicy<{commandType.Name}, TEntity> for {string.Join(" and ", rows.Select(_ => _.Name))}, so which rows it decides is ambiguous. Write one policy type per command.");
                }

                if (rows is [var entity])
                {
                    return (entity, (ICommandPolicyInvoker) Activator.CreateInstance(typeof(CommandRowsPolicyInvoker<,>).MakeGenericType(commandType, entity))!);
                }

                return (null, (ICommandPolicyInvoker) Activator.CreateInstance(typeof(CommandPolicyInvoker<>).MakeGenericType(commandType))!);
            });
}

/// <summary>
/// The typed calls that ask a command policy, reached through a class closed over the command (and the
/// row type) once rather than a <see cref="MethodInfo"/> invoked reflectively per call.
/// </summary>
interface ICommandPolicyInvoker
{
    bool HasRows { get; }

    bool Allow(object policy, ScryPolicyContext context);

    LambdaExpression? Rows(object policy, ScryPolicyContext context);
}

sealed class CommandPolicyInvoker<TCommand> :
    ICommandPolicyInvoker
{
    public bool HasRows => false;

    public bool Allow(object policy, ScryPolicyContext context) =>
        ((ICommandPolicy<TCommand>) policy).Allow(context);

    public LambdaExpression? Rows(object policy, ScryPolicyContext context) =>
        null;
}

sealed class CommandRowsPolicyInvoker<TCommand, TEntity> :
    ICommandPolicyInvoker
{
    public bool HasRows => true;

    public bool Allow(object policy, ScryPolicyContext context) =>
        ((ICommandPolicy<TCommand>) policy).Allow(context);

    public LambdaExpression? Rows(object policy, ScryPolicyContext context) =>
        ((ICommandPolicy<TCommand, TEntity>) policy).Rows(context);
}
