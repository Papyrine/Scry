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

        var properties = keyProperties.GetOrAdd(command.GetType(), _ => [.. model.Keys.Select(_.GetProperty)]);
        return [.. properties.Select(_ => ValueTag.Of(_?.GetValue(command)).Value)];
    }

    // A command type's key members, found once: the keys are its attribute's, which is per type.
    static ConcurrentDictionary<Type, PropertyInfo?[]> keyProperties = new();
}
