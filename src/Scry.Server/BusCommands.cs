namespace Scry;

/// <summary>
/// Which commands a bus adapter's dispatcher carries: those named with <see cref="For{TCommand}"/>,
/// every one with <see cref="ForAll"/>, and any its adapter claims by default. Whatever it does not
/// carry is handled in-process, by the <see cref="ICommandHandler{TCommand}"/> the container supplies.
/// </summary>
/// <remarks>
/// A command two dispatchers claim is refused at startup, as one no dispatcher and no handler claims
/// is: a command goes one way.
/// </remarks>
public sealed class BusCommands
{
    HashSet<Type> named = [];
    bool all;

    /// <summary>Carries <typeparamref name="TCommand"/>, the server's command class.</summary>
    public BusCommands For<TCommand>()
        where TCommand : class
    {
        named.Add(typeof(TCommand));
        return this;
    }

    /// <summary>Carries every command this server has.</summary>
    public BusCommands ForAll()
    {
        all = true;
        return this;
    }

    /// <summary>
    /// Whether <paramref name="command"/> is carried: named, all are, or — named or not —
    /// <paramref name="byDefault"/> says so.
    /// </summary>
    public bool Claims(Type command, Func<Type, bool>? byDefault = null) =>
        all ||
        named.Contains(command) ||
        byDefault?.Invoke(command) == true;
}
