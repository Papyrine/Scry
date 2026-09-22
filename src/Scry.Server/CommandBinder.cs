using System.Text.Json.Serialization.Metadata;

/// <summary>
/// Binds a command's payload into the server's own class, through its allow-listed properties alone:
/// a member the payload names that the class does not expose to clients is refused, a key it omits is
/// refused, an enum travels by name only, and nothing is polymorphic. Then the class's own validation
/// runs over what arrived.
/// </summary>
/// <remarks>
/// A serializer of its own rather than <see cref="ScryJson.Options"/>: those options read the wire
/// vocabulary, and what is read here is the consumer's class, restricted to what the schema says a
/// client may set. Its failures are rewritten to name wire names only — the serializer's own messages
/// name CLR types.
/// </remarks>
sealed class CommandBinder
{
    JsonSerializerOptions options;
    Dictionary<Type, CommandMeta> byType;
    ConcurrentDictionary<Type, List<PropertyInfo>> nonNullElements = new();

    public CommandBinder(IEnumerable<CommandMeta> commands)
    {
        byType = commands.ToDictionary(_ => _.ClrType);
        options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            MaxDepth = 16,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = {Restrict}
            }
        };

        // By name, and only by name: a number would be a value the enum may not define.
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
    }

    void Restrict(JsonTypeInfo type)
    {
        if (type.Kind == JsonTypeInfoKind.Object)
        {
            type.PolymorphismOptions = null;
        }

        if (!byType.TryGetValue(type.Type, out var meta))
        {
            return;
        }

        var allowed = meta.Payload.Select(_ => _.Name).ToHashSet(StringComparer.Ordinal);
        var keys = meta.TargetKeys.Select(_ => _.Payload.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = type.Properties.Count - 1; i >= 0; i--)
        {
            var property = type.Properties[i];
            if (property.AttributeProvider is not PropertyInfo clr ||
                !allowed.Contains(clr.Name))
            {
                type.Properties.RemoveAt(i);
                continue;
            }

            // The row a targeted command acts on is named by these; one that names none is refused
            // rather than bound to the default key, which is a row too.
            if (keys.Contains(clr.Name))
            {
                property.IsRequired = true;
            }
        }

        type.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    }

    /// <summary>Binds a payload into a new instance of the command's class, or refuses it.</summary>
    public object Bind(CommandMeta meta, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ScryValidationException($"The payload of command '{meta.Name}' must be a JSON object.");
        }

        object? command;
        try
        {
            command = payload.Deserialize(meta.ClrType, options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ScryValidationException(Describe(meta, payload, exception));
        }

        if (command is null)
        {
            throw new ScryValidationException($"The payload of command '{meta.Name}' must be a JSON object.");
        }

        RefuseNullElements(meta, command);
        Validate(meta, command);
        return command;
    }

    // Names what was wrong in wire terms, and never a CLR type: a member the command does not have, a
    // required one missing, or where in the payload it could not be read.
    string Describe(CommandMeta meta, JsonElement payload, Exception exception)
    {
        var info = options.GetTypeInfo(meta.ClrType);
        var known = info.Properties.Select(_ => _.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var member in payload.EnumerateObject())
        {
            if (!known.Contains(member.Name))
            {
                return $"The payload of command '{meta.Name}' carries '{Bounded(member.Name)}', which the command does not have.";
            }
        }

        var missing = info.Properties
            .Where(property => property.IsRequired && !payload.TryGetProperty(property.Name, out _))
            .Select(_ => _.Name)
            .ToList();
        if (missing.Count > 0)
        {
            return $"The payload of command '{meta.Name}' is missing {string.Join(", ", missing.Select(_ => $"'{_}'"))}.";
        }

        var path = exception is JsonException {Path: {Length: > 0} at} ? at : "$";
        return $"The payload of command '{meta.Name}' is not valid at '{Bounded(path)}'.";
    }

    // A name read off the payload goes back into a message, so it is cut to a length a message can carry.
    static string Bounded(string value)
    {
        if (value.Length <= 128)
        {
            return value;
        }

        return $"{value[..128]}…";
    }

    // RespectNullableAnnotations refuses a null where a property is declared to hold a value, but says
    // nothing about the elements of a list, so ["a", null] would reach a handler as a list holding a null
    // it was declared never to hold.
    void RefuseNullElements(CommandMeta meta, object command)
    {
        foreach (var property in nonNullElements.GetOrAdd(meta.ClrType, _ => NonNullElementProperties(meta)))
        {
            if (property.GetValue(command) is IEnumerable items &&
                items.Cast<object?>().Any(_ => _ is null))
            {
                throw new ScryValidationException($"An element of '{JsonNamingPolicy.CamelCase.ConvertName(property.Name)}' in the payload of command '{meta.Name}' cannot be null.");
            }
        }
    }

    static List<PropertyInfo> NonNullElementProperties(CommandMeta meta)
    {
        // A context of its own: one is not safe to share between threads, and this runs once per command.
        var nullability = new NullabilityInfoContext();
        var properties = new List<PropertyInfo>();
        foreach (var property in meta.Payload)
        {
            if (Schema.ExposableCollectionElement(property.PropertyType) is null)
            {
                continue;
            }

            var state = nullability.Create(property);
            var element = state.ElementType ?? state.GenericTypeArguments.FirstOrDefault();
            if (element?.ReadState == NullabilityState.NotNull)
            {
                properties.Add(property);
            }
        }

        return properties;
    }

    // The class's own rules — [Required], [Range], [StringLength] — over what the client set. A property
    // behind [CommandIgnore] is the server's to fill after this, so it is not asked.
    static void Validate(CommandMeta meta, object command)
    {
        var results = new List<ValidationResult>();
        foreach (var property in meta.Payload)
        {
            var context = new ValidationContext(command)
            {
                MemberName = property.Name
            };
            Validator.TryValidateProperty(property.GetValue(command), context, results);
        }

        if (results.Count == 0)
        {
            return;
        }

        var message = $"The payload of command '{meta.Name}' is not valid: {string.Join(" ", results.Select(_ => _.ErrorMessage))}";
        if (message.Length > 1024)
        {
            message = $"{message[..1024]}…";
        }

        throw new ScryValidationException(message);
    }
}
