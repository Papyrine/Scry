/// <summary>
/// The <c>[ScryCommand]</c> a generated command class carries, read once per type: which command an
/// instance is, and the members carrying its target's key.
/// </summary>
static class ScryCommandModels
{
    static ConcurrentDictionary<Type, ScryCommandAttribute?> models = new();

    public static ScryCommandAttribute Of(Type type) =>
        models.GetOrAdd(type, _ => _.GetCustomAttribute<ScryCommandAttribute>(inherit: false)) ??
        throw new NotSupportedException(
            $"'{type.Name}' carries no [ScryCommand] naming the command it is. A generated command class carries one: send commands through the generated ScryCommands, or declare it on a hand-written class.");

    /// <summary>
    /// The target key a command carries, each value in the spelling a query constant of it travels in —
    /// what a pending-work panel names the row by. Empty for an untargeted command.
    /// </summary>
    public static IReadOnlyList<string?> Keys(object command, ScryCommandAttribute model)
    {
        if (model.Keys.Length == 0)
        {
            return [];
        }

        var type = command.GetType();
        return [.. model.Keys.Select(_ => ValueTag.Of(type.GetProperty(_)?.GetValue(command)).Value)];
    }
}
