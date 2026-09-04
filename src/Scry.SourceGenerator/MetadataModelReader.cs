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

    public static ModelExtract Read(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return ModelExtract.Empty;
        }

        try
        {
            // ReSharper disable once RedundantSuppressNullableWarningExpression
            var bytes = File.ReadAllBytes(dllPath!);
            using var pe = new PEReader([..bytes]);
            var reader = pe.GetMetadataReader();

            var decoder = new SignatureDecoder();
            var discovered = new List<Discovered>();
            var conflicts = ImmutableArray.CreateBuilder<string>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (!TryClassify(reader, type, decoder, out var kind, out var sourceName, out var conflict))
                {
                    if (conflict is not null)
                    {
                        conflicts.Add($"'{reader.GetString(type.Name)}' carries {conflict}");
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

            return new(null, new(DeriveKeys(WithoutInheritedMembers(sources))), new(enums.Values.ToImmutableArray()), new(conflicts.ToImmutable()));
        }
        catch (Exception exception)
        {
            return new($"Failed to read model assembly '{dllPath}': {exception.Message}", new([]), new([]), new([]));
        }
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

            result.Add(source with {Keys = new(Keys(source, members))});
        }

        return result.ToImmutable();
    }

    // A member of the row's key is a scalar the client can name and read: an attachment is neither, and
    // a navigation or collection is not a value. [Key] wins where it is written, since it is the only
    // one of the three that was stated rather than inferred.
    static ImmutableArray<string> Keys(SourceInfo source, List<PropertyInfo> members)
    {
        var candidates = members
            .Where(_ => _ is {IsNavigation: false, IsCollection: false, IsAttachment: false})
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
                Properties = new(source.Properties.Where(_ => !inherited.Contains(_.Name)).ToImmutableArray())
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
