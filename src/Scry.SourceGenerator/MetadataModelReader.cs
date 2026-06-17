/// <summary>
/// Reads the allow-listed query surface from a server model assembly using
/// <see cref="MetadataReader"/> — metadata only, never loading or executing the assembly.
/// </summary>
static class MetadataModelReader
{
    const string queryableAttribute = "Scry.QueryableAttribute";
    const string queryableViewAttribute = "Scry.QueryableViewAttribute";
    const string queryablePocoAttribute = "Scry.QueryablePocoAttribute";
    const string queryIgnoreAttribute = "Scry.QueryIgnoreAttribute";
    const string keylessAttribute = "Microsoft.EntityFrameworkCore.KeylessAttribute";

    public static ModelExtract Read(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return ModelExtract.Empty;
        }

        try
        {
            var bytes = File.ReadAllBytes(dllPath!);
            using var pe = new PEReader([..bytes]);
            var reader = pe.GetMetadataReader();

            var discovered = new List<Discovered>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (TryClassify(reader, type, out var kind, out var sourceName))
                {
                    var simpleName = reader.GetString(type.Name);
                    var fullName = FullName(reader, type);
                    discovered.Add(new(handle, type, fullName, $"{simpleName}QueryModel", kind, sourceName));
                }
            }

            var modelByFullName = discovered.ToDictionary(_ => _.FullName, _ => _.ModelName, StringComparer.Ordinal);
            var decoder = new SignatureDecoder();
            var enums = new Dictionary<string, EnumInfo>(StringComparer.Ordinal);

            var sources = ImmutableArray.CreateBuilder<SourceInfo>();
            foreach (var entry in discovered)
            {
                var properties = ReadProperties(reader, entry.Type, decoder, modelByFullName, enums);
                sources.Add(new(entry.SourceName, entry.ModelName, entry.Kind, new(properties)));
            }

            return new(null, new(sources.ToImmutable()), new(enums.Values.ToImmutableArray()));
        }
        catch (Exception exception)
        {
            return new($"Failed to read model assembly '{dllPath}': {exception.Message}", new([]), new([]));
        }
    }

    static ImmutableArray<PropertyInfo> ReadProperties(
        MetadataReader reader,
        TypeDefinition type,
        SignatureDecoder decoder,
        Dictionary<string, string> modelByFullName,
        Dictionary<string, EnumInfo> enums)
    {
        var properties = ImmutableArray.CreateBuilder<PropertyInfo>();
        foreach (var propertyHandle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            if (!HasPublicInstanceGetter(reader, property) ||
                HasAttribute(reader, property.GetCustomAttributes(), queryIgnoreAttribute))
            {
                continue;
            }

            var signature = property.DecodeSignature(decoder, genericContext: null);
            if (Classify(reader, signature.ReturnType, modelByFullName, enums) is { } info)
            {
                properties.Add(info with { Name = reader.GetString(property.Name) });
            }
        }

        return properties.ToImmutable();
    }

    static PropertyInfo? Classify(
        MetadataReader reader,
        DecodedType type,
        Dictionary<string, string> modelByFullName,
        Dictionary<string, EnumInfo> enums)
    {
        var nullable = false;
        if (type is NullableDecoded outer)
        {
            nullable = true;
            type = outer.Inner;
        }

        switch (type)
        {
            case PrimitiveDecoded primitive when PrimitiveKeyword(primitive.Code) is { } keyword:
                if (keyword == "string")
                {
                    return new("", "string", NeedsNullDefault: true);
                }

                return new("", nullable ? $"{keyword}?" : keyword, NeedsNullDefault: false);

            case NamedDecoded named when ScalarKeyword(named.FullName) is { } scalar:
                if (scalar == "string")
                {
                    return new("", "string", NeedsNullDefault: true);
                }

                return new("", nullable ? $"{scalar}?" : scalar, NeedsNullDefault: false);

            case NamedDecoded { IsDefinition: true } definition when IsEnum(reader, (TypeDefinitionHandle)definition.Handle):
                var enumName = CollectEnum(reader, (TypeDefinitionHandle)definition.Handle, enums);
                return new("", nullable ? $"{enumName}?" : enumName, NeedsNullDefault: false);

            case NamedDecoded navigation when modelByFullName.TryGetValue(navigation.FullName, out var modelName):
                // Reference navigation to another queryable type: nullable, no initializer.
                return new("", $"{modelName}?", NeedsNullDefault: false);

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

        var members = ImmutableArray.CreateBuilder<string>();
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if ((field.Attributes & FieldAttributes.Literal) != 0)
            {
                members.Add(reader.GetString(field.Name));
            }
        }

        enums[name] = new(name, new(members.ToImmutable()));
        return name;
    }

    static bool TryClassify(MetadataReader reader, TypeDefinition type, out SourceKind kind, out string sourceName)
    {
        kind = default;
        sourceName = reader.GetString(type.Name);

        SourceKind? found = null;
        var keyless = false;

        foreach (var attributeHandle in type.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            switch (AttributeTypeName(reader, attribute))
            {
                case queryableAttribute:
                    found ??= SourceKind.Entity;
                    break;
                case queryableViewAttribute:
                    found = SourceKind.View;
                    break;
                case queryablePocoAttribute:
                    found = SourceKind.Poco;
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

        kind = sourceKind == SourceKind.Entity && keyless ? SourceKind.View : sourceKind;
        return true;
    }

    static bool IsEnum(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        if (definition.BaseType.IsNil || definition.BaseType.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var baseType = reader.GetTypeReference((TypeReferenceHandle)definition.BaseType);
        return reader.GetString(baseType.Namespace) == "System" && reader.GetString(baseType.Name) == "Enum";
    }

    static bool HasPublicInstanceGetter(MetadataReader reader, PropertyDefinition property)
    {
        var getter = property.GetAccessors().Getter;
        if (getter.IsNil)
        {
            return false;
        }

        var method = reader.GetMethodDefinition(getter);
        return (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
               (method.Attributes & MethodAttributes.Static) == 0;
    }

    static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection attributes, string fullName)
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

    static string Combine(string ns, string name) =>
        ns.Length == 0 ? name : $"{ns}.{name}";

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
        TypeDefinitionHandle Handle,
        TypeDefinition Type,
        string FullName,
        string ModelName,
        SourceKind Kind,
        string SourceName);
}
