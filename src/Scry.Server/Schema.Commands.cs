sealed partial class Schema
{
    Dictionary<string, CommandMeta> commands = new(StringComparer.Ordinal);

    public bool TryGetCommand(string name, [MaybeNullWhen(false)] out CommandMeta command) =>
        commands.TryGetValue(name, out command);

    /// <summary>Every command, ordered by name.</summary>
    internal IEnumerable<CommandMeta> Commands =>
        commands.Values.OrderBy(_ => _.Name, StringComparer.Ordinal);

    /// <summary>
    /// Reads every <c>[Command]</c> class in the model assembly: its name, payload, target, result and
    /// policy. A targeted command's key is bound to payload properties and its capability added to the
    /// target and every type deriving from it. Refuses, with a directed message, each thing the generator
    /// reports as SCRY009–017 — the two readers have to agree about what a command is, or every client
    /// reports itself stale.
    /// </summary>
    /// <remarks>
    /// Must stay in lockstep with <c>MetadataModelReader.ReadCommands</c>. Only the model assembly is read,
    /// as only it is read by the generator.
    /// </remarks>
    static void BuildCommands(Schema schema, Type contextType, ScryOptions options)
    {
        var assembly = contextType.Assembly;
        var found = assembly.GetTypes()
            .Select(_ => (Type: _, Attribute: _.GetCustomAttribute<CommandAttribute>(inherit: false)))
            .Where(_ => _.Attribute is not null)
            .Select(_ => (_.Type, Attribute: _.Attribute!, Name: Named(_.Attribute!.Name, _.Type.Name)))
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var registered in options.CommandPolicies.Keys)
        {
            if (found.All(_ => _.Type != registered))
            {
                throw new($"A command policy is registered for '{registered.Name}', which carries no [Command]. Opt the class in, or remove the registration.");
            }
        }

        foreach (var meta in schema.types.Values)
        {
            if (meta.Members.Values.FirstOrDefault(_ => _.Property?.HasAttribute<CommandIgnoreAttribute>() == true) is { } ignored)
            {
                throw new($"'{meta.ClrType.Name}.{ignored.Name}' carries [CommandIgnore], which keeps a property out of a command's payload and means nothing on a query model. Use [QueryIgnore] to hide a member from queries.");
            }
        }

        var results = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var (type, attribute, name) in found)
        {
            EnsureConcreteCommand(type);
            if (!CSharpIdentifier.IsValid(name))
            {
                throw new($"The command name '{name}' on '{type.Name}' cannot be written as a C# member name. A command name is also the method the generated facade sends it with, so it has to be one C# can express. Set [Command(Name = \"...\")] to a plain identifier that is not a reserved keyword.");
            }

            if (schema.commands.TryGetValue(name, out var claimed))
            {
                throw new($"Two commands are named '{name}': '{claimed.ClrType.Name}' and '{type.Name}'. Set a distinct [Command(Name = \"...\")] on one of them.");
            }

            var payload = CommandPayload(type, assembly);

            ScrySource? target = null;
            IReadOnlyList<(Member Key, PropertyInfo Payload)> keys = [];
            if (attribute.Target is { } targetType)
            {
                if (!schema.sourcesByType.TryGetValue(targetType, out var source) ||
                    source.Kind != SourceKind.Entity)
                {
                    throw new($"'{type.Name}' targets '{targetType.Name}', which is not a [Queryable] entity. A targeted command acts on one row, read by its key through the entity's policies, so its target has to be an opted-in entity: a view, a POCO or a complex type has no key to read the row by.");
                }

                target = source;
                keys = BindCommandKeys(type, schema.types[targetType], payload);
            }

            IReadOnlyList<PropertyInfo> resultProperties = [];
            if (attribute.Result is { } result)
            {
                resultProperties = ResultProperties(type, result, assembly);
                if (results.TryGetValue(result.Name, out var existing) &&
                    existing != result)
                {
                    throw new($"Two result classes are named '{result.Name}': '{existing.FullName}' and '{result.FullName}'. Every result class is emitted into the generated client by its simple name, so each needs a name of its own.");
                }

                results[result.Name] = result;
            }

            var policy = options.CommandPolicies.GetValueOrDefault(type) ?? attribute.Policy;
            Type? rows = null;
            if (policy is not null)
            {
                rows = CommandPolicy.RowsEntity(policy, type);
                if (rows is not null &&
                    (target is null || !rows.IsAssignableFrom(target.ClrType)))
                {
                    var why = target is null
                        ? "it is untargeted, so there are no rows for it to decide"
                        : $"'{target.ClrType.Name}' does not derive from '{rows.Name}'";
                    throw new($"Command policy '{policy.Name}' decides rows of '{rows.Name}' for '{type.Name}', but {why}. Write the row interface against the command's target, or one of its bases.");
                }
            }

            Member? capability = null;
            if (target is not null)
            {
                capability = Member.Capability(name);
                AddCapability(schema, target.ClrType, capability, type);
            }

            schema.commands[name] = new(name, type)
            {
                Target = target,
                TargetKeys = keys,
                Payload = payload,
                Result = attribute.Result,
                ResultProperties = resultProperties,
                Policy = policy,
                PolicyRows = rows,
                Capability = capability,
                Obsolete = ObsoleteOf(type),

                // A server with commands off accepts none, so none reads as sendable — on the facade or
                // on any row — whatever its policy would have said.
                Available = options.MaxPendingCommands > 0
            };
        }

        EnsureGeneratedNamesDistinct(schema, results.Keys);
    }

    // What the server binds a payload into has to be something it can create: a class, not abstract,
    // not generic, with a public constructor taking nothing. Mirrors the generator's SCRY017.
    internal static void EnsureConcreteCommand(Type type)
    {
        if (type is {IsClass: true, IsAbstract: false, ContainsGenericParameters: false} &&
            type.GetConstructor(Type.EmptyTypes) is not null)
        {
            return;
        }

        throw new($"'{type.Name}' carries [Command] but is not a concrete class with a public parameterless constructor. A command is bound into a new instance of its class, so it has to be a class that can be created: not abstract, not generic, not a struct.");
    }

    /// <summary>
    /// The properties a payload may set: public, readable and writable, not indexers, not marked
    /// <c>[CommandIgnore]</c>. Ordered by name, which is the order they are described in.
    /// </summary>
    internal static List<PropertyInfo> CommandPayload(Type type, Assembly assembly)
    {
        var payload = new List<PropertyInfo>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is not {IsPublic: true} ||
                property.SetMethod is not {IsPublic: true} ||
                property.GetIndexParameters().Length > 0 ||
                property.HasAttribute<CommandIgnoreAttribute>())
            {
                continue;
            }

            if (property.HasAttribute<QueryIgnoreAttribute>())
            {
                throw new($"'{type.Name}.{property.Name}' carries [QueryIgnore], which hides a member from queries and means nothing on a command. Use [CommandIgnore] to keep a property out of the payload.");
            }

            EnsureDeclaredInModel(type, property, assembly);
            if (!IsValueShape(property.PropertyType, assembly))
            {
                throw new($"'{type.Name}.{property.Name}' is a '{property.PropertyType.Name}', which a command cannot carry. A payload property is a scalar, an enum declared in the model, a byte[], a nullable of those, or a list of them; anything the server fills itself belongs behind [CommandIgnore].");
            }

            payload.Add(property);
        }

        return [.. payload.OrderBy(_ => _.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The properties a result is described by: its public readable ones. Every one has to be a value a
    /// client can read back, and the class has to be the model's, which is all the generator reads.
    /// </summary>
    internal static List<PropertyInfo> ResultProperties(Type command, Type result, Assembly assembly)
    {
        if (result.Assembly != assembly)
        {
            throw new($"'{command.Name}' answers with '{result.Name}', which is declared in assembly '{result.Assembly.GetName().Name}'. A client is generated from the model assembly alone, so a result class has to be declared there too.");
        }

        var properties = new List<PropertyInfo>();
        foreach (var property in result.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is not {IsPublic: true} ||
                property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            EnsureDeclaredInModel(result, property, assembly);
            if (!IsValueShape(property.PropertyType, assembly))
            {
                throw new($"'{command.Name}' answers with '{result.Name}', whose property '{property.Name}' is a '{property.PropertyType.Name}', which a result cannot carry. A result property is a scalar, an enum declared in the model, a byte[], a nullable of those, or a list of them.");
            }

            properties.Add(property);
        }

        return [.. properties.OrderBy(_ => _.Name, StringComparer.Ordinal)];
    }

    // The generator reads a class's members out of the model assembly's metadata, so a property inherited
    // from a base anywhere else is one it never sees — and a client missing a payload property the server
    // expects is a stale client on every build.
    static void EnsureDeclaredInModel(Type type, PropertyInfo property, Assembly assembly)
    {
        if (property.DeclaringType is not { } declaring ||
            declaring.Assembly == assembly)
        {
            return;
        }

        throw new($"'{type.Name}.{property.Name}' is inherited from '{declaring.Name}' in assembly '{declaring.Assembly.GetName().Name}'. A client is generated from the model assembly's metadata alone, so it could never see the property. Declare it on a type in the model assembly, or keep it out of the payload with [CommandIgnore].");
    }

    // A scalar, an enum the generator can re-emit, a byte[], a nullable of those, or a collection of them
    // in a shape the generator reads. Mirrors MetadataModelReader.ClassifyValue.
    static bool IsValueShape(Type type, Assembly assembly)
    {
        Type? value = null;
        if (IsScalar(type))
        {
            value = type;
        }
        else if (ExposableCollectionElement(type) is { } element &&
                 IsScalar(element))
        {
            value = element;
        }

        if (value is null)
        {
            return false;
        }

        var underlying = Nullable.GetUnderlyingType(value) ?? value;
        return !underlying.IsEnum ||
               underlying.Assembly == assembly;
    }

    /// <summary>
    /// The payload properties carrying the target's key, in the target's key order: for each key member,
    /// the one property named like it or prefixed with the target's type name, typed as it is.
    /// </summary>
    internal static List<(Member Key, PropertyInfo Payload)> BindCommandKeys(Type command, TypeMeta target, List<PropertyInfo> payload)
    {
        var keys = DeriveKeys(target);
        var entity = target.ClrType.Name;
        if (keys.Count == 0)
        {
            throw new($"'{command.Name}' targets '{entity}', which has no derivable key. A targeted command names its row by key: mark the key member(s) with [Key], or name a member 'Id' or '{entity}Id'.");
        }

        var bound = new List<(Member, PropertyInfo)>(keys.Count);
        foreach (var key in keys)
        {
            var prefixed = $"{entity}{key.Name}";
            var candidates = payload
                .Where(_ => _.Name == key.Name || _.Name == prefixed)
                .ToList();
            if (candidates.Count > 1)
            {
                throw new($"'{command.Name}' carries both '{key.Name}' and '{prefixed}', so which one is the key of '{entity}' is ambiguous. Keep one, or mark the other [CommandIgnore].");
            }

            if (candidates is not [var candidate] ||
                candidate.PropertyType != key.Type)
            {
                throw new($"'{command.Name}' targets '{entity}', keyed by '{key.Name}', but carries no '{ScalarDisplay(key.Type)}' property named '{key.Name}' or '{prefixed}'. A targeted command carries its row's key, named after the key member or prefixed with the entity's name, and typed as the key is.");
            }

            bound.Add((key, candidate));
        }

        return bound;
    }

    /// <summary>
    /// Adds a command's capability to its target and every type deriving from it — reflection reports a
    /// base's members on its subclasses, and a capability is a member like any other there — refusing a
    /// name one of them already answers to.
    /// </summary>
    static void AddCapability(Schema schema, Type target, Member capability, Type command)
    {
        var metas = schema.types.Values
            .Where(_ => target.IsAssignableFrom(_.ClrType))
            .ToList();
        foreach (var meta in metas)
        {
            if (meta.Members.ContainsKey(capability.Name) ||
                meta.PreviousNames.ContainsKey(capability.Name))
            {
                throw new($"'{command.Name}' would add '{capability.Name}' to '{meta.ClrType.Name}', which already answers to that name. Rename the member, or give the command a different Name.");
            }
        }

        foreach (var meta in metas)
        {
            meta.Members[capability.Name] = capability;
        }
    }

    /// <summary>
    /// Refuses a name the generated client would declare twice — every command, result class, query
    /// model and enum is a class in one namespace, and the facade declares a method and a <c>Can</c>
    /// property per command. Mirrors the generator's SCRY011, and checks nothing for a model with no
    /// commands, which it declares nothing new for.
    /// </summary>
    static void EnsureGeneratedNamesDistinct(Schema schema, IEnumerable<string> results)
    {
        if (schema.commands.Count == 0)
        {
            return;
        }

        var types = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ScryQuery"] = "the query entry point",
            ["ScryCommands"] = "the command facade"
        };
        foreach (var meta in schema.types.Values)
        {
            types.TryAdd($"{meta.ClrType.Name}QueryModel", $"the query model for '{meta.ClrType.Name}'");
        }

        var (_, _, enums, _) = schema.DescribeSurface();
        foreach (var enumeration in enums)
        {
            Declare(types, enumeration.Name, "an enum");
        }

        foreach (var result in results)
        {
            Declare(types, result, "a result class");
        }

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in schema.commands.Values)
        {
            Declare(types, command.Name, $"the command '{command.ClrType.Name}'");
            Declare(members, command.Name, $"the facade method for '{command.ClrType.Name}'");
            Declare(members, $"Can{command.Name}", $"the facade capability for '{command.ClrType.Name}'");
        }

        static void Declare(Dictionary<string, string> declared, string name, string what)
        {
            if (!declared.TryAdd(name, what))
            {
                throw new($"The name '{name}' would be generated twice: as {declared[name]} and as {what}. Every command, result class, query model and enum is emitted into the generated client's namespace, and every command adds a method and a 'Can' property to the facade, so each needs a name of its own. Set [Command(Name = \"...\")] on the command, or rename one of the two.");
            }
        }
    }

    /// <summary>
    /// Projects the commands into the introspection contract. Property displays mirror the generator's
    /// emission exactly, as a query model's do, since the stamp hashes them.
    /// </summary>
    List<ScryCommandInfo> DescribeCommands(Dictionary<string, ScryEnumInfo> enums) =>
        commands.Values
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .Select(command => new ScryCommandInfo(
                command.Name,
                command.Payload.Select(_ => DescribeValue(_, enums)).ToList())
            {
                Target = command.Target?.Name,
                Keys = command.Target is null ? null : command.TargetKeys.Select(_ => _.Payload.Name).ToList(),
                Result = command.Result is { } result
                    ? new ScryResultInfo(result.Name, command.ResultProperties.Select(_ => DescribeValue(_, enums)).ToList())
                    : null,
                Obsolete = command.Obsolete
            })
            .ToList();

    // How a payload or result property is spelled in generated code. Mirrors
    // MetadataModelReader.ClassifyValue: a value as a query model would spell it, or a list of one.
    static ScryMemberInfo DescribeValue(PropertyInfo property, Dictionary<string, ScryEnumInfo> enums)
    {
        var type = property.PropertyType;
        if (IsScalar(type))
        {
            var shape = ScalarShape(type, enums);
            return new(property.Name, shape, NeedsNullDefault: shape is "string" or "byte[]", IsNavigation: false)
            {
                Obsolete = ObsoleteOf(property)
            };
        }

        var element = ExposableCollectionElement(type)!;
        return new(
            property.Name,
            $"global::System.Collections.Generic.IReadOnlyList<{ScalarShape(element, enums)}>",
            NeedsNullDefault: true,
            IsNavigation: false)
        {
            Obsolete = ObsoleteOf(property)
        };
    }

    /// <summary>
    /// Confirms the key each command target was derived to have is the key EF really gives it — the same
    /// check an attachment's key gets, for the same reason: the derivation reads annotations and naming
    /// conventions, because the generator has to reach the same answer and never sees
    /// <c>OnModelCreating</c>. A target absent from the model is left to the mapping check.
    /// </summary>
    void ValidateCommandKeys(IModel model, Type contextType)
    {
        foreach (var command in commands.Values)
        {
            if (command.Target is not { } target ||
                model.FindEntityType(target.ClrType) is not { } entity)
            {
                continue;
            }

            var actual = entity.FindPrimaryKey()?.Properties.Select(_ => _.Name).ToList() ?? [];
            var expected = command.TargetKeys.Select(_ => _.Key.Name).ToList();
            if (actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            {
                continue;
            }

            var entityName = target.ClrType.Name;
            var describe = actual.Count == 0 ? "no primary key" : $"a primary key of ({string.Join(", ", actual)})";
            throw new($"'{command.ClrType.Name}' targets '{entityName}' by a key derived as ({string.Join(", ", expected)}), but {contextType.Name} gives '{entityName}' {describe}. The derivation reads [Key] and the 'Id'/'{entityName}Id' conventions, because a client is generated from the model's metadata and never sees OnModelCreating. Mark the key member(s) with [Key] to state it where both sides can read it.");
        }
    }
}
