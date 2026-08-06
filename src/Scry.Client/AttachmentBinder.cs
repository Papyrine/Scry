/// <summary>
/// Fills a materialized row's attachment members with handles. The value is never in the payload —
/// no query reads it — so the row deserializes with those members empty and they are filled here,
/// from the key that came back beside them.
/// </summary>
/// <remarks>
/// <para>
/// Runs after ordinary deserialization rather than instead of it, so enum aliases, binary parts, and
/// every other reader behaviour still apply exactly as they do to a row with no attachment.
/// </para>
/// <para>
/// Reflection over the projected type is what this costs. A generated model's members are
/// <c>init</c>, which reflection can still assign; an anonymous type's cannot be assigned at all, so
/// that row is rebuilt through its constructor. Both are cached per type. Under aggressive trimming a
/// generator-emitted binder would replace this, but the payload path already reflects over these same
/// types, so nothing here is reachable that deserialization did not already require.
/// </para>
/// </remarks>
static class AttachmentBinder
{
    static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> properties = new();
    static readonly ConcurrentDictionary<Type, ConstructorInfo?> constructors = new();

    /// <summary>Binds every row of a materialized result, returning it with handles in place.</summary>
    public static T? Bind<T>(T? result, AttachmentPlan? plan, ScryClient client)
    {
        if (plan is null ||
            result is null)
        {
            return result;
        }

        // A list is rebound in place: the rows may be immutable, but the list holding them is the one
        // just deserialized and is not shared with anything.
        if (result is IList list and not Array)
        {
            for (var i = 0; i < list.Count; i++)
            {
                list[i] = Row(list[i], plan, client);
            }

            return result;
        }

        return (T?) Row(result, plan, client);
    }

    /// <summary>Binds one row — the streaming and single-result path.</summary>
    public static T? BindRow<T>(T? row, AttachmentPlan? plan, ScryClient client) =>
        plan is null || row is null ? row : (T?) Row(row, plan, client);

    static object? Row(object? row, AttachmentPlan plan, ScryClient client)
    {
        if (row is null)
        {
            return null;
        }

        foreach (var binding in plan.Bindings)
        {
            // Read before anything is replaced. The keys are ordinary projected members, so they are
            // already parsed into their own CLR types and are re-tagged exactly as a constant of that
            // type would be — the server parses both the same way.
            var keys = new List<AttachmentKey>(binding.KeySources.Count);
            foreach (var source in binding.KeySources)
            {
                var (value, tag) = ValueTag.Of(Read(row, source));
                keys.Add(new(value, tag));
            }

            row = Write(row, binding.Target, 0, new ScryAttachment(client, binding.Root, binding.Member, keys));
        }

        return row;
    }

    static object? Read(object? instance, IReadOnlyList<string> path)
    {
        foreach (var segment in path)
        {
            if (instance is null)
            {
                return null;
            }

            instance = Property(instance.GetType(), segment)?.GetValue(instance);
        }

        return instance;
    }

    // Walks to the object owning the member and sets it, rebuilding each object on the way back up
    // when it turns out to be immutable — replacing a member of an anonymous type produces a new one,
    // so its owner has to be replaced too.
    static object Write(object instance, IReadOnlyList<string> path, int index, object value)
    {
        var name = path[index];
        if (index < path.Count - 1)
        {
            // A nested object that came back null (the navigation had no row) has no member to fill,
            // and no handle would be meaningful on it.
            if (Property(instance.GetType(), name)?.GetValue(instance) is not { } nested)
            {
                return instance;
            }

            value = Write(nested, path, index + 1, value);
        }

        return Set(instance, name, value);
    }

    static object Set(object instance, string name, object value)
    {
        var type = instance.GetType();
        var property = Property(type, name);
        if (property is null)
        {
            return instance;
        }

        // An init-only setter is an ordinary setter to reflection, so a generated model binds without
        // being rebuilt.
        if (property.SetMethod is not null)
        {
            property.SetValue(instance, value);
            return instance;
        }

        // No setter at all: an anonymous type, or a positional record's compiler-generated property.
        // Its values arrived through the constructor, so a replacement means constructing again.
        if (Constructor(type) is not { } constructor)
        {
            throw new NotSupportedException(
                $"'{type.Name}.{name}' is an attachment member that cannot be assigned: the type has neither a setter for it nor a single constructor to rebuild it with. Project into an anonymous type, a record, or a type with settable members.");
        }

        var parameters = constructor.GetParameters();
        var arguments = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            arguments[i] = string.Equals(parameters[i].Name, name, StringComparison.OrdinalIgnoreCase)
                ? value
                : Property(type, parameters[i].Name!)?.GetValue(instance);
        }

        return constructor.Invoke(arguments);
    }

    static PropertyInfo? Property(Type type, string name) =>
        properties.GetOrAdd(
            (type, name),
            key => key.Item1.GetProperty(key.Item2, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));

    static ConstructorInfo? Constructor(Type type) =>
        constructors.GetOrAdd(
            type,
            _ => _.GetConstructors() is [var single] ? single : null);
}
