/// <summary>
/// Reads the allow-listed query surface from a server model assembly using
/// <see cref="MetadataReader"/> — metadata only, never loading or executing the assembly.
/// </summary>
static class MetadataModelReader
{
    const string queryableAttribute = "Scry.QueryableAttribute";
    const string queryableViewAttribute = "Scry.QueryableViewAttribute";
    const string queryablePocoAttribute = "Scry.QueryablePocoAttribute";
    const string queryableComplexAttribute = "Scry.QueryableComplexAttribute";
    const string queryIgnoreAttribute = "Scry.QueryIgnoreAttribute";
    const string queryableCollectionAttribute = "Scry.QueryableCollectionAttribute";
    const string keylessAttribute = "Microsoft.EntityFrameworkCore.KeylessAttribute";
    const string obsoleteAttribute = "System.ObsoleteAttribute";
    const string attachmentAttribute = "Scry.AttachmentAttribute";
    const string binaryTransferAttribute = "Scry.BinaryTransferAttribute";
    const string keyAttribute = "System.ComponentModel.DataAnnotations.KeyAttribute";
    const string sensitiveAttribute = "Scry.SensitiveAttribute";
    const string flagsAttribute = "System.FlagsAttribute";
    const string commandAttribute = "Scry.CommandAttribute";
    const string commandIgnoreAttribute = "Scry.CommandIgnoreAttribute";

    public static ModelExtract Read(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
        {
            return ModelExtract.Empty;
        }

        // The generator runs inside the compiler process, whose working directory is not the
        // project's. The shipped targets resolve the path against the project before it becomes
        // compiler-visible; one that arrives relative anyway was wired by hand, and would otherwise
        // read as a file that does not exist and generate nothing, silently.
        if (!Path.IsPathRooted(dllPath))
        {
            return new(
                $"ScryModelDll '{dllPath}' is a relative path. The generator runs inside the compiler process, whose working directory is not the project's, so the path must be absolute: resolve it with $([MSBuild]::NormalizePath('$(MSBuildProjectDirectory)', '$(ScryModelDll)')) before it becomes compiler-visible, as the Scry.Client targets do.",
                [],
                [],
                []);
        }

        if (!File.Exists(dllPath))
        {
            return ModelExtract.Empty;
        }

        try
        {
            // Read off the file rather than out of a byte array: only the metadata block is needed,
            // and an array would be copied once more to become the immutable one the reader takes.
            // ReSharper disable once RedundantSuppressNullableWarningExpression
            using var pe = new PEReader(File.OpenRead(dllPath!));
            var reader = pe.GetMetadataReader();

            var decoder = new SignatureDecoder();
            var discovered = new List<Discovered>();
            var conflicts = ImmutableArray.CreateBuilder<string>();
            var commandTypes = new List<TypeDefinition>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (!TryClassify(reader, type, decoder, out var kind, out var sourceName, out var conflict))
                {
                    if (conflict is not null)
                    {
                        conflicts.Add($"'{reader.GetString(type.Name)}' carries {conflict}");
                    }
                    else if (HasAttribute(reader, type.GetCustomAttributes(), commandAttribute))
                    {
                        commandTypes.Add(type);
                    }

                    continue;
                }

                {
                    var simpleName = reader.GetString(type.Name);
                    var fullName = FullName(reader, type);
                    discovered.Add(
                        new(
                            type,
                            fullName,
                            $"{simpleName}QueryModel",
                            kind,
                            sourceName,
                            ObsoleteOf(reader, type.GetCustomAttributes(), decoder),
                            simpleName,
                            HasAttribute(reader, type.GetCustomAttributes(), sensitiveAttribute)));
                }
            }

            var modelByFullName = discovered.ToDictionary(_ => _.FullName, _ => _.ModelName, StringComparer.Ordinal);
            var discoveredByFullName = discovered.ToDictionary(_ => _.FullName, StringComparer.Ordinal);
            var enums = new Dictionary<string, EnumInfo>(StringComparer.Ordinal);

            var sources = ImmutableArray.CreateBuilder<SourceInfo>();
            foreach (var entry in discovered)
            {
                var properties = ReadProperties(reader, entry.Type, decoder, modelByFullName, enums, discoveredByFullName);
                sources.Add(
                    new(
                        entry.SourceName,
                        entry.ModelName,
                        entry.Kind,
                        new(properties),
                        NearestOptedInBase(reader, entry.Type, discoveredByFullName),
                        entry.Obsolete,
                        entry.ClrName,
                        IsSensitive: entry.IsSensitive));
            }

            var catalog = ReadCommands(reader, commandTypes, decoder, sources, discoveredByFullName, enums);
            return new(
                null,
                new(DeriveKeys(WithoutInheritedMembers(sources))),
                new(enums.Values.ToImmutableArray()),
                new(conflicts.ToImmutable()),
                new(catalog.Commands),
                new(catalog.Results),
                new(catalog.Problems));
        }
        catch (Exception exception)
        {
            return new($"Failed to read model assembly '{dllPath}': {exception.Message}", new([]), new([]), new([]));
        }
    }

    /// <summary>
    /// Reads every <c>[Command]</c> class: its name, payload, target and result. A targeted command's
    /// key is bound to payload properties, and the capability member it earns is added to its target.
    /// Every way one is misdeclared is recorded as a problem rather than guessed past — the server
    /// refuses the same model at startup, and the two readers have to agree about what a command is.
    /// </summary>
    /// <remarks>
    /// Must stay in lockstep with <c>Schema</c>'s command catalog, which reads the same attribute over
    /// reflection. Run before <see cref="WithoutInheritedMembers"/>, so a capability is declared by its
    /// target and inherited by the target's derived models.
    /// </remarks>
    static (ImmutableArray<CommandInfo> Commands, ImmutableArray<ResultInfo> Results, ImmutableArray<CommandProblem> Problems) ReadCommands(
        MetadataReader reader,
        List<TypeDefinition> commandTypes,
        SignatureDecoder decoder,
        ImmutableArray<SourceInfo>.Builder sources,
        Dictionary<string, Discovered> discovered,
        Dictionary<string, EnumInfo> enums)
    {
        var problems = ImmutableArray.CreateBuilder<CommandProblem>();
        if (commandTypes.Count == 0)
        {
            return ([], [], problems.ToImmutable());
        }

        var definitions = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            definitions[FullName(reader, definition)] = definition;
        }

        var commands = new List<CommandInfo>();
        var results = new Dictionary<string, (string FullName, ResultInfo Info)>(StringComparer.Ordinal);

        // Ordered by name, so the capabilities a target gains land in the same order on every build.
        var ordered = commandTypes
            .Select(_ => (Type: _, Arguments: CommandArguments(reader, _, decoder)))
            .OrderBy(_ => _.Arguments.Name ?? reader.GetString(_.Type.Name), StringComparer.Ordinal)
            .ToList();
        foreach (var (type, arguments) in ordered)
        {
            var clrName = reader.GetString(type.Name);
            var name = arguments.Name ?? clrName;

            if (!IsConcrete(reader, type, decoder))
            {
                problems.Add(
                    new(
                        "SCRY017",
                        $"'{clrName}' carries [Command] but is not a concrete class with a public parameterless constructor. A command is bound into a new instance of its class on the server, so it has to be a class that can be created: not abstract, not generic, not a struct."));
                continue;
            }

            if (!CSharpIdentifier.IsValid(name))
            {
                problems.Add(
                    new(
                        "SCRY012",
                        $"The command name '{name}' on '{clrName}' cannot be written as a C# member name, so the facade method sending it cannot be generated. Set [Command(Name = \"...\")] to a plain identifier that is not a reserved keyword."));
                continue;
            }

            var before = problems.Count;
            var properties = ReadCommandProperties(reader, type, decoder, enums, clrName, problems);

            string? target = null;
            var keys = ImmutableArray<string>.Empty;
            SourceInfo? targetSource = null;
            if (arguments.Target is { } targetName)
            {
                if (!discovered.TryGetValue(targetName, out var entry) ||
                    entry.Kind != SourceKind.Entity)
                {
                    problems.Add(
                        new(
                            "SCRY009",
                            $"'{clrName}' targets '{SimpleName(targetName)}', which is not a [Queryable] entity. A targeted command acts on one row, read by its key through the entity's policies, so its target has to be an opted-in entity: a view, a POCO or a complex type has no key to read the row by."));
                    continue;
                }

                var byModel = ByModel(sources);
                var source = byModel[entry.ModelName];
                keys = BindKeys(clrName, source, Inherited(source, byModel), properties, problems);
                target = entry.SourceName;
                targetSource = source;
            }

            string? resultName = null;
            if (arguments.Result is { } resultType)
            {
                resultName = ReadResult(reader, resultType, clrName, definitions, decoder, enums, results, problems);
            }

            if (problems.Count > before)
            {
                continue;
            }

            if (targetSource is { } capabilityTarget &&
                !AddCapability(sources, capabilityTarget, name, clrName, problems))
            {
                continue;
            }

            commands.Add(
                new(
                    name,
                    clrName,
                    target,
                    new(keys),
                    new(properties),
                    resultName,
                    ObsoleteOf(reader, type.GetCustomAttributes(), decoder)));
        }

        return (
            [.. commands.OrderBy(_ => _.Name, StringComparer.Ordinal)],
            [.. results.Values.Select(_ => _.Info).OrderBy(_ => _.Name, StringComparer.Ordinal)],
            problems.ToImmutable());
    }

    static Dictionary<string, SourceInfo> ByModel(ImmutableArray<SourceInfo>.Builder sources)
    {
        var byModel = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            byModel[source.ModelName] = source;
        }

        return byModel;
    }

    /// <summary>
    /// The <c>[Command]</c> attribute's arguments: the name override, the target's full name, and the
    /// result's. Any the attribute does not carry — or a blob that cannot be read — is null.
    /// </summary>
    static (string? Name, string? Target, string? Result) CommandArguments(MetadataReader reader, TypeDefinition type, SignatureDecoder decoder)
    {
        foreach (var handle in type.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (AttributeTypeName(reader, attribute) != commandAttribute)
            {
                continue;
            }

            try
            {
                var value = attribute.DecodeValue(decoder);
                string? target = null;
                if (value.FixedArguments is [{Value: SerializedTypeDecoded {FullName: { } targetName}} _])
                {
                    target = targetName;
                }

                string? name = null;
                string? result = null;
                foreach (var argument in value.NamedArguments)
                {
                    if (argument is {Name: "Name", Value: string configured} &&
                        !string.IsNullOrWhiteSpace(configured))
                    {
                        name = configured;
                    }
                    else if (argument is {Name: "Result", Value: SerializedTypeDecoded {FullName: { } resultName}})
                    {
                        result = resultName;
                    }
                }

                return (name, target, result);
            }
            catch (BadImageFormatException)
            {
            }
        }

        return (null, null, null);
    }

    // A class the server can create: not an interface, not abstract (a static class is both abstract and
    // sealed), not generic, not a value type, and carrying a public constructor that takes nothing.
    static bool IsConcrete(MetadataReader reader, TypeDefinition type, SignatureDecoder decoder)
    {
        if ((type.Attributes & (TypeAttributes.Interface | TypeAttributes.Abstract)) != 0 ||
            type.GetGenericParameters().Count > 0 ||
            TypeName(reader, type.BaseType) is "System.ValueType" or "System.Enum")
        {
            return false;
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) == ".ctor" &&
                (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                (method.Attributes & MethodAttributes.Static) == 0 &&
                method.DecodeSignature(decoder, genericContext: null).ParameterTypes.Length == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A command's payload: its public readable and writable instance properties, and those of every
    /// base in this assembly, less any marked <c>[CommandIgnore]</c>. Base-most first, a name declared
    /// more than once being one property described by its nearest declaration, as reflection reads it.
    /// </summary>
    static ImmutableArray<PropertyInfo> ReadCommandProperties(
        MetadataReader reader,
        TypeDefinition type,
        SignatureDecoder decoder,
        Dictionary<string, EnumInfo> enums,
        string clrName,
        ImmutableArray<CommandProblem>.Builder problems)
    {
        var properties = ImmutableArray.CreateBuilder<PropertyInfo>();
        foreach (var (property, attributes) in PropertiesAlongTheChain(reader, type))
        {
            if (!HasPublicInstanceGetter(reader, property) ||
                !HasPublicInstanceSetter(reader, property) ||
                HasAttribute(reader, attributes, commandIgnoreAttribute))
            {
                continue;
            }

            var signature = property.DecodeSignature(decoder, genericContext: null);
            if (signature.ParameterTypes.Length > 0)
            {
                continue;
            }

            var name = reader.GetString(property.Name);
            if (HasAttribute(reader, attributes, queryIgnoreAttribute))
            {
                problems.Add(
                    new(
                        "SCRY013",
                        $"'{clrName}.{name}' carries [QueryIgnore], which hides a member from queries and means nothing on a command. Use [CommandIgnore] to keep a property out of the payload."));
                continue;
            }

            if (ClassifyValue(reader, signature.ReturnType, enums) is not { } classified)
            {
                problems.Add(
                    new(
                        "SCRY014",
                        $"'{clrName}.{name}' is not a type a command can carry. A payload property is a scalar, an enum declared in the model, a byte[], a nullable of those, or a list of them; anything the server fills itself belongs behind [CommandIgnore]."));
                continue;
            }

            properties.Add(
                classified with
                {
                    Name = name,
                    Obsolete = ObsoleteOf(reader, attributes, decoder)
                });
        }

        return properties.ToImmutable();
    }

    // Every property of the type and of its bases in this assembly, base-most first. A name declared more
    // than once is one property, described by its nearest declaration and carrying every declaration's
    // attributes — the walk DeclaredProperties makes, without its stop at an opted-in base.
    static List<(PropertyDefinition Property, List<CustomAttributeHandle> Attributes)> PropertiesAlongTheChain(
        MetadataReader reader,
        TypeDefinition type)
    {
        var levels = new List<List<(PropertyDefinition, List<CustomAttributeHandle>)>>();
        var byName = new Dictionary<string, List<CustomAttributeHandle>>(StringComparer.Ordinal);
        var current = type;
        while (true)
        {
            var level = new List<(PropertyDefinition, List<CustomAttributeHandle>)>();
            foreach (var handle in current.GetProperties())
            {
                var property = reader.GetPropertyDefinition(handle);
                var name = reader.GetString(property.Name);
                var attributes = property.GetCustomAttributes().ToList();
                if (byName.TryGetValue(name, out var nearer))
                {
                    nearer.AddRange(attributes);
                    continue;
                }

                byName[name] = attributes;
                level.Add((property, attributes));
            }

            levels.Add(level);
            if (current.BaseType.IsNil ||
                current.BaseType.Kind != HandleKind.TypeDefinition)
            {
                break;
            }

            current = reader.GetTypeDefinition((TypeDefinitionHandle)current.BaseType);
        }

        levels.Reverse();
        return levels.SelectMany(_ => _).ToList();
    }

    /// <summary>
    /// How a command or result property is spelled in generated code, or null for a type neither can
    /// carry: a scalar, an enum from this assembly, a byte[], a nullable of those, or a list of them.
    /// Spelled exactly as the same value would be on a query model, which is what keeps the server's
    /// description of it identical.
    /// </summary>
    static PropertyInfo? ClassifyValue(MetadataReader reader, DecodedType type, Dictionary<string, EnumInfo> enums)
    {
        if (Classify(reader, type, noModels, enums, collectionOptIn: false) is {IsNavigation: false, IsCollection: false} scalar)
        {
            return scalar;
        }

        if (type is CollectionDecoded collection &&
            Classify(reader, collection.Element, noModels, enums, collectionOptIn: false) is {IsNavigation: false, IsCollection: false} element)
        {
            return new("", $"global::System.Collections.Generic.IReadOnlyList<{element.TypeDisplay}>", NeedsNullDefault: true);
        }

        return null;
    }

    static Dictionary<string, string> noModels = new(StringComparer.Ordinal);

    /// <summary>
    /// The payload properties carrying the target's key, in the target's key order: for each key member,
    /// the one property named like it or prefixed with the target's type name, typed as it is.
    /// </summary>
    static ImmutableArray<string> BindKeys(
        string clrName,
        SourceInfo target,
        List<PropertyInfo> targetMembers,
        ImmutableArray<PropertyInfo> properties,
        ImmutableArray<CommandProblem>.Builder problems)
    {
        var keys = Keys(target, targetMembers);
        if (keys.Length == 0)
        {
            problems.Add(
                new(
                    "SCRY010",
                    $"'{clrName}' targets '{target.ClrName}', which has no derivable key. A targeted command names its row by key: mark the key member(s) with [Key], or name a member 'Id' or '{target.ClrName}Id'."));
            return [];
        }

        var bound = ImmutableArray.CreateBuilder<string>();
        foreach (var key in keys)
        {
            var member = targetMembers.First(_ => _.Name == key);
            var prefixed = $"{target.ClrName}{key}";
            var candidates = properties
                .Where(_ => _.Name == key || _.Name == prefixed)
                .ToList();
            if (candidates.Count > 1)
            {
                problems.Add(
                    new(
                        "SCRY010",
                        $"'{clrName}' carries both '{key}' and '{prefixed}', so which one is the key of '{target.ClrName}' is ambiguous. Keep one, or mark the other [CommandIgnore]."));
                continue;
            }

            if (candidates is not [var candidate] ||
                candidate.TypeDisplay != member.TypeDisplay)
            {
                problems.Add(
                    new(
                        "SCRY010",
                        $"'{clrName}' targets '{target.ClrName}', keyed by '{key}', but carries no '{member.TypeDisplay}' property named '{key}' or '{prefixed}'. A targeted command carries its row's key, named after the key member or prefixed with the entity's name, and typed as the key is."));
                continue;
            }

            bound.Add(candidate.Name);
        }

        return bound.ToImmutable();
    }

    /// <summary>
    /// Reads the class a command answers with, or records why it cannot be one, returning the name it is
    /// emitted as. A class several commands share is read once.
    /// </summary>
    static string? ReadResult(
        MetadataReader reader,
        string resultFullName,
        string clrName,
        Dictionary<string, TypeDefinition> definitions,
        SignatureDecoder decoder,
        Dictionary<string, EnumInfo> enums,
        Dictionary<string, (string FullName, ResultInfo Info)> results,
        ImmutableArray<CommandProblem>.Builder problems)
    {
        if (!definitions.TryGetValue(resultFullName, out var type))
        {
            problems.Add(
                new(
                    "SCRY015",
                    $"'{clrName}' answers with '{SimpleName(resultFullName)}', which is not declared in the model assembly. A client is generated from that assembly alone, so a result class has to be declared there too."));
            return null;
        }

        var name = reader.GetString(type.Name);
        if (results.TryGetValue(name, out var existing))
        {
            if (existing.FullName == resultFullName)
            {
                return name;
            }

            problems.Add(
                new(
                    "SCRY011",
                    $"Two result classes are named '{name}'. Every result class is emitted into Scry.Generated by its simple name, so each needs a name of its own."));
            return null;
        }

        var properties = ImmutableArray.CreateBuilder<PropertyInfo>();
        var valid = true;
        foreach (var (property, attributes) in PropertiesAlongTheChain(reader, type))
        {
            if (!HasPublicInstanceGetter(reader, property))
            {
                continue;
            }

            var signature = property.DecodeSignature(decoder, genericContext: null);
            if (signature.ParameterTypes.Length > 0)
            {
                continue;
            }

            var propertyName = reader.GetString(property.Name);
            if (ClassifyValue(reader, signature.ReturnType, enums) is not { } classified)
            {
                problems.Add(
                    new(
                        "SCRY015",
                        $"'{clrName}' answers with '{name}', whose property '{propertyName}' is not a type a result can carry. A result property is a scalar, an enum declared in the model, a byte[], a nullable of those, or a list of them."));
                valid = false;
                continue;
            }

            properties.Add(
                classified with
                {
                    Name = propertyName,
                    Obsolete = ObsoleteOf(reader, attributes, decoder)
                });
        }

        if (!valid)
        {
            return null;
        }

        results[name] = (resultFullName, new(name, new(properties.ToImmutable())));
        return name;
    }

    /// <summary>
    /// Adds <c>Can{Command}</c> to the target's declared members, or records why it cannot be added: a
    /// member of that name on the target, a base of it, or a model deriving from it.
    /// </summary>
    static bool AddCapability(
        ImmutableArray<SourceInfo>.Builder sources,
        SourceInfo target,
        string command,
        string clrName,
        ImmutableArray<CommandProblem>.Builder problems)
    {
        var capability = $"Can{command}";
        var byModel = ByModel(sources);
        var current = byModel[target.ModelName];
        var collides = Inherited(current, byModel).Any(_ => _.Name == capability) ||
                       sources.Any(_ => _.ModelName != target.ModelName &&
                                        DerivesFrom(_, target.ModelName, byModel) &&
                                        _.Properties.Any(property => property.Name == capability));
        if (collides)
        {
            problems.Add(
                new(
                    "SCRY016",
                    $"'{clrName}' would add '{capability}' to '{target.ClrName}', which already has a member of that name. Rename the member, or give the command a different Name."));
            return false;
        }

        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i].ModelName != target.ModelName)
            {
                continue;
            }

            sources[i] = current with
            {
                Properties = [with(current.Properties.Array.Add(new(capability, "bool", NeedsNullDefault: false, Capability: command)))]
            };
            break;
        }

        return true;
    }

    static bool DerivesFrom(SourceInfo source, string model, Dictionary<string, SourceInfo> byModel)
    {
        var current = source;
        while (current.BaseModelName is { } baseName &&
               byModel.TryGetValue(baseName, out var baseSource))
        {
            if (baseName == model)
            {
                return true;
            }

            current = baseSource;
        }

        return false;
    }

    static string SimpleName(string fullName)
    {
        var dot = fullName.LastIndexOf('.');
        if (dot < 0)
        {
            return fullName;
        }

        return fullName.Substring(dot + 1);
    }

    static bool HasPublicInstanceSetter(MetadataReader reader, PropertyDefinition property)
    {
        var setter = property.GetAccessors().Setter;
        if (setter.IsNil)
        {
            return false;
        }

        var attributes = reader.GetMethodDefinition(setter).Attributes;
        return (attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
               (attributes & MethodAttributes.Static) == 0;
    }

    /// <summary>
    /// Fills in <see cref="SourceInfo.Keys"/> for every model carrying an attachment. An attachment is
    /// fetched by the row's key, so the key has to be nameable on the client — but fluent
    /// configuration is invisible here (the assembly is read, never run), so the key is derived by
    /// EF's own conventions and the server verifies the answer against the real model at startup.
    /// </summary>
    /// <remarks>
    /// Must stay in lockstep with <c>Schema.DeriveKeys</c>, which repeats this over reflection. A
    /// disagreement is not a compile error on either side: it is a client that names one key and a
    /// server that expects another.
    /// </remarks>
    static ImmutableArray<SourceInfo> DeriveKeys(ImmutableArray<SourceInfo>.Builder sources)
    {
        var byModel = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            byModel[source.ModelName] = source;
        }

        var result = ImmutableArray.CreateBuilder<SourceInfo>(sources.Count);
        foreach (var source in sources)
        {
            var members = Inherited(source, byModel);
            if (!members.Any(_ => _.IsAttachment))
            {
                result.Add(source);
                continue;
            }

            result.Add(source with
            {
                Keys = [with(Keys(source, members))]
            });
        }

        return result.ToImmutable();
    }

    // A member of the row's key is a scalar the client can name and read: an attachment is neither, and
    // a navigation or collection is not a value. Nor is a capability, which the server computes rather
    // than stores. [Key] wins where it is written, since it is the only one of the three that was
    // stated rather than inferred.
    static ImmutableArray<string> Keys(SourceInfo source, List<PropertyInfo> members)
    {
        var candidates = members
            .Where(_ => _ is {IsNavigation: false, IsCollection: false, IsAttachment: false, Capability: null})
            .ToList();

        var declared = candidates
            .Where(_ => _.IsKey)
            .Select(_ => _.Name)
            .OrderBy(_ => _, StringComparer.Ordinal)
            .ToImmutableArray();
        if (declared.Length > 0)
        {
            return declared;
        }

        foreach (var convention in new[] {"Id", $"{source.ClrName}Id"})
        {
            if (candidates.Any(_ => string.Equals(_.Name, convention, StringComparison.Ordinal)))
            {
                return [convention];
            }
        }

        // No key derivable. Reported as SCRY007 by the generator, which is where a diagnostic has a
        // model name to attribute it to.
        return [];
    }

    // The members a model exposes, its inherited ones included — the same base-first walk the emitted
    // default projection makes, since a key declared on a base is still the derived row's key.
    static List<PropertyInfo> Inherited(SourceInfo source, Dictionary<string, SourceInfo> byModel)
    {
        var members = new List<PropertyInfo>();
        if (source.BaseModelName is { } baseName &&
            byModel.TryGetValue(baseName, out var baseSource))
        {
            members.AddRange(Inherited(baseSource, byModel));
        }

        members.AddRange(source.Properties);
        return members;
    }

    // Walks up the base chain to the first type that was itself opted in, skipping any that were not —
    // so leaving a base out hides it without hiding its descendants. Must stay in lockstep with
    // Schema's own base-linking, which does the same walk over reflection.
    static string? NearestOptedInBase(
        MetadataReader reader,
        TypeDefinition type,
        Dictionary<string, Discovered> discovered)
    {
        var current = type;
        while (true)
        {
            // A base outside this assembly is a TypeReference, which cannot have been opted in here —
            // the walk ends rather than trying to follow it.
            if (current.BaseType.IsNil ||
                current.BaseType.Kind != HandleKind.TypeDefinition)
            {
                return null;
            }

            current = reader.GetTypeDefinition((TypeDefinitionHandle)current.BaseType);
            if (discovered.TryGetValue(FullName(reader, current), out var match))
            {
                return match.ModelName;
            }
        }
    }

    static ImmutableArray<PropertyInfo> ReadProperties(
        MetadataReader reader,
        TypeDefinition type,
        SignatureDecoder decoder,
        Dictionary<string, string> modelByFullName,
        Dictionary<string, EnumInfo> enums,
        Dictionary<string, Discovered> discovered)
    {
        var properties = ImmutableArray.CreateBuilder<PropertyInfo>();
        foreach (var (property, attributes) in DeclaredProperties(reader, type, discovered))
        {
            if (!HasPublicInstanceGetter(reader, property) ||
                HasAttribute(reader, attributes, queryIgnoreAttribute))
            {
                continue;
            }

            var signature = property.DecodeSignature(decoder, genericContext: null);

            // An indexer is a property with parameters, which no query names; reflection leaves it
            // out on the server too.
            if (signature.ParameterTypes.Length > 0)
            {
                continue;
            }

            var collectionOptIn = HasAttribute(reader, attributes, queryableCollectionAttribute);
            var attachment = HasAttribute(reader, attributes, attachmentAttribute);
            var classified = Classify(reader, signature.ReturnType, modelByFullName, enums, collectionOptIn);

            // An attachment whose type is not one Classify recognizes is still carried, with the empty
            // display standing for "not a byte[]". Dropping it silently would leave the misapplied
            // attribute to be discovered at server startup instead of at the build that wrote it.
            if (classified is null && !attachment)
            {
                continue;
            }

            var info = classified ?? new("", "", NeedsNullDefault: false);
            properties.Add(
                info with
                {
                    Name = reader.GetString(property.Name),
                    Obsolete = ObsoleteOf(reader, attributes, decoder),
                    IsAttachment = attachment,
                    HasBinaryTransfer = HasAttribute(reader, attributes, binaryTransferAttribute),
                    IsKey = HasAttribute(reader, attributes, keyAttribute),
                    IsSensitive = HasAttribute(reader, attributes, sensitiveAttribute)
                });
        }

        return properties.ToImmutable();
    }

    // The properties a model exposes as its own: the type's, and those of every base in this assembly
    // that did not opt in — the server reads inherited members by reflection, and a base that opted
    // in is the generated model's own base instead, so the walk stops there. A name declared more
    // than once along the chain (an override) is one member, described by its nearest declaration
    // and carrying the attributes of every declaration, as reflection's inherit walk reads them.
    static List<(PropertyDefinition Property, List<CustomAttributeHandle> Attributes)> DeclaredProperties(
        MetadataReader reader,
        TypeDefinition type,
        Dictionary<string, Discovered> discovered)
    {
        var levels = new List<List<(PropertyDefinition, List<CustomAttributeHandle>)>>();
        var byName = new Dictionary<string, List<CustomAttributeHandle>>(StringComparer.Ordinal);
        var current = type;
        while (true)
        {
            var level = new List<(PropertyDefinition, List<CustomAttributeHandle>)>();
            foreach (var handle in current.GetProperties())
            {
                var property = reader.GetPropertyDefinition(handle);
                var name = reader.GetString(property.Name);
                var attributes = property.GetCustomAttributes().ToList();
                if (byName.TryGetValue(name, out var nearer))
                {
                    nearer.AddRange(attributes);
                    continue;
                }

                byName[name] = attributes;
                level.Add((property, attributes));
            }

            levels.Add(level);

            // A base outside this assembly is a TypeReference, which cannot be read here; the server
            // refuses a member inherited from one, so the walk ending is what keeps the two aligned.
            if (current.BaseType.IsNil ||
                current.BaseType.Kind != HandleKind.TypeDefinition)
            {
                break;
            }

            current = reader.GetTypeDefinition((TypeDefinitionHandle)current.BaseType);
            if (discovered.ContainsKey(FullName(reader, current)))
            {
                break;
            }
        }

        // Base-most first, as a class lays its inherited members out.
        levels.Reverse();
        return levels.SelectMany(_ => _).ToList();
    }

    // A member an opted-in base already declares is the base model's, inherited by the derived model
    // rather than declared again: an override is two declarations in metadata and one member in
    // reflection, and the server describes it on the base alone (Schema.Declared). Declaring it again
    // would also hide the inherited member, which the consumer's build warns about.
    static ImmutableArray<SourceInfo>.Builder WithoutInheritedMembers(ImmutableArray<SourceInfo>.Builder sources)
    {
        var byModel = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            byModel[source.ModelName] = source;
        }

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (source.BaseModelName is not { } baseName ||
                !byModel.TryGetValue(baseName, out var baseSource))
            {
                continue;
            }

            var inherited = new HashSet<string>(Inherited(baseSource, byModel).Select(_ => _.Name), StringComparer.Ordinal);
            sources[i] = source with
            {
                Properties = [with(source.Properties.Where(_ => !inherited.Contains(_.Name)).ToImmutableArray())]
            };
        }

        return sources;
    }

    static PropertyInfo? Classify(
        MetadataReader reader,
        DecodedType type,
        Dictionary<string, string> modelByFullName,
        Dictionary<string, EnumInfo> enums,
        bool collectionOptIn)
    {
        var nullable = false;
        if (type is NullableDecoded outer)
        {
            nullable = true;
            type = outer.Inner;
        }

        switch (type)
        {
            case PrimitiveDecoded primitive
                when PrimitiveKeyword(primitive.Code) is { } keyword:
                if (keyword == "string")
                {
                    return new("", "string", NeedsNullDefault: true);
                }

                return new("", nullable ? $"{keyword}?" : keyword, NeedsNullDefault: false);

            case NamedDecoded named
                when ScalarKeyword(named.FullName) is { } scalar:
                if (scalar == "string")
                {
                    return new("", "string", NeedsNullDefault: true);
                }

                return new("", nullable ? $"{scalar}?" : scalar, NeedsNullDefault: false);

            // The only array shape Scry exposes. Like string it is a reference type, so a
            // non-nullable byte[] needs ' = null!;'. Mirrors Schema.ScalarDisplay's "System.Byte[]".
            case BytesDecoded:
                return new("", "byte[]", NeedsNullDefault: true);

            case NamedDecoded {IsDefinition: true} definition
                when IsEnum(reader, (TypeDefinitionHandle) definition.Handle):
                var enumName = CollectEnum(reader, (TypeDefinitionHandle) definition.Handle, enums);
                return new("", nullable ? $"{enumName}?" : enumName, NeedsNullDefault: false);

            case NamedDecoded navigation
                when modelByFullName.TryGetValue(navigation.FullName, out var modelName):
                // Reference navigation to another queryable type: nullable, no initializer.
                return new("", $"{modelName}?", NeedsNullDefault: false, IsNavigation: true);

            // A collection navigation, exposed only when the member opted in and its element is itself
            // a queryable type. Emitted as a read-only list: it is aggregated, never assigned.
            // Mirrors Schema.DescribeMember, which the schema stamp requires to be identical.
            case CollectionDecoded {Element: NamedDecoded element}
                when collectionOptIn && modelByFullName.TryGetValue(element.FullName, out var elementModel):
                return new(
                    "",
                    $"global::System.Collections.Generic.IReadOnlyList<{elementModel}>",
                    NeedsNullDefault: true,
                    IsCollection: true);

            // A collection of values — an EF primitive collection. The element is classified as if it
            // were a member's own type, which is what keeps its spelling (and any enum it reaches)
            // identical to the scalar case, as Schema.ScalarShape does on the reflection side.
            case CollectionDecoded collection
                when collectionOptIn &&
                     Classify(reader, collection.Element, modelByFullName, enums, collectionOptIn: false) is
                         {IsNavigation: false, IsCollection: false} scalarElement:
                return new(
                    "",
                    $"global::System.Collections.Generic.IReadOnlyList<{scalarElement.TypeDisplay}>",
                    NeedsNullDefault: true,
                    IsCollection: true);

            default:
                return null;
        }
    }

    static string CollectEnum(MetadataReader reader, TypeDefinitionHandle handle, Dictionary<string, EnumInfo> enums)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        if (enums.ContainsKey(name))
        {
            return name;
        }

        // Each member's value is read with its name: re-emitted without it, every member past the
        // first explicit one would hold a different value from the model's. Must stay in lockstep
        // with Schema.DescribeEnum, which describes the same enum by reflection.
        var members = ImmutableArray.CreateBuilder<string>();
        var values = ImmutableArray.CreateBuilder<string>();
        var underlying = "int";
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Literal) == 0)
            {
                continue;
            }

            members.Add(reader.GetString(field.Name));
            var (keyword, value) = ReadConstant(reader, reader.GetConstant(field.GetDefaultValue()));
            underlying = keyword;
            values.Add(value);
        }

        var flags = HasAttribute(reader, definition.GetCustomAttributes(), flagsAttribute);
        enums[name] = new(name, underlying, flags, new(members.ToImmutable()), new(values.ToImmutable()));
        return name;
    }

    // An enum member's constant, as the keyword of its type and the decimal the declaration spells.
    static (string Keyword, string Value) ReadConstant(MetadataReader reader, Constant constant)
    {
        var blob = reader.GetBlobReader(constant.Value);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return constant.TypeCode switch
        {
            ConstantTypeCode.SByte => ("sbyte", blob.ReadSByte().ToString(culture)),
            ConstantTypeCode.Byte => ("byte", blob.ReadByte().ToString(culture)),
            ConstantTypeCode.Int16 => ("short", blob.ReadInt16().ToString(culture)),
            ConstantTypeCode.UInt16 => ("ushort", blob.ReadUInt16().ToString(culture)),
            ConstantTypeCode.Int32 => ("int", blob.ReadInt32().ToString(culture)),
            ConstantTypeCode.UInt32 => ("uint", blob.ReadUInt32().ToString(culture)),
            ConstantTypeCode.Int64 => ("long", blob.ReadInt64().ToString(culture)),
            ConstantTypeCode.UInt64 => ("ulong", blob.ReadUInt64().ToString(culture)),
            _ => throw new NotSupportedException($"An enum member of constant type '{constant.TypeCode}' is not supported.")
        };
    }

    static bool TryClassify(
        MetadataReader reader,
        TypeDefinition type,
        SignatureDecoder decoder,
        out SourceKind kind,
        out string sourceName,
        out string? conflict)
    {
        kind = default;
        sourceName = reader.GetString(type.Name);
        conflict = null;

        SourceKind? found = null;
        var keyless = false;
        string? configuredName = null;
        List<string>? optIns = null;

        foreach (var attributeHandle in type.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            switch (AttributeTypeName(reader, attribute))
            {
                case queryableAttribute:
                    found = SourceKind.Entity;
                    configuredName = NameArgument(attribute, decoder);
                    (optIns ??= []).Add("[Queryable]");
                    break;
                case queryableViewAttribute:
                    found = SourceKind.View;
                    configuredName = NameArgument(attribute, decoder);
                    (optIns ??= []).Add("[QueryableView]");
                    break;
                case queryablePocoAttribute:
                    found = SourceKind.Poco;
                    configuredName = NameArgument(attribute, decoder);
                    (optIns ??= []).Add("[QueryablePoco]");
                    break;
                case queryableComplexAttribute:
                    // A complex type has no Name (it is not a source); its model name is the type name.
                    found = SourceKind.Complex;
                    (optIns ??= []).Add("[QueryableComplex]");
                    break;
                case keylessAttribute:
                    keyless = true;
                    break;
                case commandAttribute:
                    // Counted so a type that is both a source and a command is refused as a conflict.
                    // A command alone is not a source, and is read apart from them.
                    (optIns ??= []).Add("[Command]");
                    break;
            }
        }

        if (found is not { } sourceKind)
        {
            return false;
        }

        // A type opts in as exactly one thing. Read as nothing rather than as whichever attribute
        // came last: the server reads them in an order of its own, and the two would disagree about
        // what the type is, with a stale stamp as the only symptom. Reported by the generator instead.
        if (optIns!.Count > 1)
        {
            conflict = string.Join(" and ", optIns);
            return false;
        }

        if (configuredName is not null)
        {
            sourceName = configuredName;
        }

        kind = sourceKind == SourceKind.Entity && keyless ? SourceKind.View : sourceKind;
        return true;
    }

    /// <summary>
    /// Reads the <c>Name</c> named argument off a queryable attribute, or null when it is absent or
    /// blank. A malformed attribute blob is treated as "no name" rather than failing the build.
    /// </summary>
    static string? NameArgument(CustomAttribute attribute, SignatureDecoder decoder)
    {
        try
        {
            foreach (var argument in attribute.DecodeValue(decoder).NamedArguments)
            {
                if (argument is
                    {
                        Name: "Name",
                        Value: string value
                    } &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (BadImageFormatException)
        {
        }

        return null;
    }

    /// <summary>
    /// The deprecation carried by <c>[Obsolete]</c>: null when absent, otherwise the message, or empty
    /// when the attribute gave none. Only the message is read — the <c>error</c> flag is deliberately
    /// dropped, because an obsolete member is still one the server will happily execute a query
    /// against, and turning a server-side annotation into an unfixable client build break would say
    /// otherwise. <c>[QueryIgnore]</c> is the hard stop.
    /// </summary>
    /// <remarks>
    /// Must stay in lockstep with Schema.ObsoleteOf, which reads the same attribute over reflection.
    /// A malformed attribute blob is treated as a bare deprecation rather than failing the build,
    /// matching how <see cref="NameArgument"/> handles one.
    /// </remarks>
    static string? ObsoleteOf(
        MetadataReader reader,
        IEnumerable<CustomAttributeHandle> attributes,
        SignatureDecoder decoder)
    {
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (AttributeTypeName(reader, attribute) != obsoleteAttribute)
            {
                continue;
            }

            try
            {
                // The message is the first fixed argument on every overload that takes one; the
                // parameterless overload has none.
                if (attribute.DecodeValue(decoder).FixedArguments is [{Value: string message}, ..] &&
                    !string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
            catch (BadImageFormatException)
            {
            }

            return "";
        }

        return null;
    }

    static bool IsEnum(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        if (definition.BaseType.IsNil ||
            definition.BaseType.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var baseType = reader.GetTypeReference((TypeReferenceHandle)definition.BaseType);
        return reader.GetString(baseType.Namespace) == "System" &&
               reader.GetString(baseType.Name) == "Enum";
    }

    static bool HasPublicInstanceGetter(MetadataReader reader, PropertyDefinition property)
    {
        var getter = property.GetAccessors().Getter;
        if (getter.IsNil)
        {
            return false;
        }

        var method = reader.GetMethodDefinition(getter);
        var attributes = method.Attributes;
        return (attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
               (attributes & MethodAttributes.Static) == 0;
    }

    static bool HasAttribute(MetadataReader reader, IEnumerable<CustomAttributeHandle> attributes, string fullName)
    {
        foreach (var handle in attributes)
        {
            if (AttributeTypeName(reader, reader.GetCustomAttribute(handle)) == fullName)
            {
                return true;
            }
        }

        return false;
    }

    static string? AttributeTypeName(MetadataReader reader, CustomAttribute attribute) =>
        attribute.Constructor.Kind switch
        {
            HandleKind.MethodDefinition =>
                TypeName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
            HandleKind.MemberReference =>
                TypeName(reader, reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
            _ => null
        };

    static string? TypeName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var definition = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                return Combine(reader.GetString(definition.Namespace), reader.GetString(definition.Name));
            case HandleKind.TypeReference:
                var reference = reader.GetTypeReference((TypeReferenceHandle)handle);
                return Combine(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
            default:
                return null;
        }
    }

    static string FullName(MetadataReader reader, TypeDefinition type) =>
        Combine(reader.GetString(type.Namespace), reader.GetString(type.Name));

    static string Combine(string ns, string name)
    {
        if (ns.Length == 0)
        {
            return name;
        }

        return $"{ns}.{name}";
    }

    static string? PrimitiveKeyword(PrimitiveTypeCode code) =>
        code switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.String => "string",
            _ => null
        };

    static string? ScalarKeyword(string fullName) =>
        fullName switch
        {
            "System.String" => "string",
            "System.Decimal" => "decimal",
            "System.DateTime" => "global::System.DateTime",
            "System.DateOnly" => "global::System.DateOnly",
            "System.TimeOnly" => "global::System.TimeOnly",
            "System.DateTimeOffset" => "global::System.DateTimeOffset",
            "System.TimeSpan" => "global::System.TimeSpan",
            "System.Guid" => "global::System.Guid",
            _ => null
        };

    readonly record struct Discovered(
        TypeDefinition Type,
        string FullName,
        string ModelName,
        SourceKind Kind,
        string SourceName,
        string? Obsolete,
        string ClrName,
        bool IsSensitive);
}
