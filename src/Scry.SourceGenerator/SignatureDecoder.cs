/// <summary>A minimal decoded representation of a property's type, enough to classify and emit it.</summary>
abstract record DecodedType;

sealed record PrimitiveDecoded(PrimitiveTypeCode Code) :
    DecodedType;

sealed record NamedDecoded(string FullName, EntityHandle Handle, bool IsDefinition) :
    DecodedType;

sealed record NullableDecoded(DecodedType Inner) :
    DecodedType;

/// <summary>A <c>byte[]</c> property — the only array shape Scry exposes, as a binary scalar.</summary>
sealed record BytesDecoded :
    DecodedType;

/// <summary>
/// A collection of <paramref name="Element"/>. Kept only far enough to see whether the element is an
/// opted-in model; a member is still exposed only when it also asks to be.
/// </summary>
sealed record CollectionDecoded(DecodedType Element) :
    DecodedType;

/// <summary>Anything Scry does not expose (multi-dimensional arrays, generics other than Nullable and collections).</summary>
sealed record OtherDecoded :
    DecodedType;

/// <summary>
/// The value of a <c>typeof(...)</c> attribute argument: the type's full name as the attribute blob
/// spells it, less any assembly qualification, or null for a null argument.
/// </summary>
sealed record SerializedTypeDecoded(string? FullName) :
    DecodedType;

/// <summary>
/// Decodes property type signatures into <see cref="DecodedType"/> using
/// <see cref="System.Reflection.Metadata"/>, recognizing only the shapes Scry cares about. Also
/// serves as the custom-attribute type provider, which is only ever asked to decode the
/// <c>string Name</c> named argument on the queryable attributes and the message on <c>[Obsolete]</c>.
/// </summary>
sealed class SignatureDecoder :
    ISignatureTypeProvider<DecodedType, object?>,
    ICustomAttributeTypeProvider<DecodedType>
{
    static OtherDecoded other = new();
    static BytesDecoded bytes = new();

    public DecodedType GetPrimitiveType(PrimitiveTypeCode typeCode) => new PrimitiveDecoded(typeCode);

    public DecodedType GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var definition = r.GetTypeDefinition(handle);
        return new NamedDecoded(Combine(r.GetString(definition.Namespace), r.GetString(definition.Name)), handle, true);
    }

    public DecodedType GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = r.GetTypeReference(handle);
        return new NamedDecoded(Combine(r.GetString(reference.Namespace), r.GetString(reference.Name)), handle, false);
    }

    public DecodedType GetGenericInstantiation(DecodedType genericType, ImmutableArray<DecodedType> typeArguments)
    {
        if (genericType is not NamedDecoded named ||
            typeArguments.Length != 1)
        {
            return other;
        }

        if (named.FullName == "System.Nullable`1")
        {
            return new NullableDecoded(typeArguments[0]);
        }

        if (collectionTypes.Contains(named.FullName))
        {
            return new CollectionDecoded(typeArguments[0]);
        }

        return other;
    }

    // The one-argument collection shapes an EF navigation is declared as. Matched by name because the
    // model assembly is read as metadata — there is no type system here to ask about assignability.
    // Shared with the server, which refuses any other shape so the two never disagree about a member.
    static HashSet<string> collectionTypes = CollectionShapes.GenericDefinitions;

    // byte[] is the one array that is a value in its own right; every other one-dimensional array is
    // a collection of its element, exactly as the server reads it.
    public DecodedType GetSZArrayType(DecodedType elementType) =>
        elementType switch
        {
            PrimitiveDecoded { Code: PrimitiveTypeCode.Byte } => bytes,
            OtherDecoded => other,
            _ => new CollectionDecoded(elementType)
        };

    public DecodedType GetArrayType(DecodedType elementType, ArrayShape shape) => other;

    public DecodedType GetByReferenceType(DecodedType elementType) => other;

    public DecodedType GetPointerType(DecodedType elementType) => other;

    public DecodedType GetFunctionPointerType(MethodSignature<DecodedType> signature) => other;

    public DecodedType GetGenericMethodParameter(object? genericContext, int index) => other;

    public DecodedType GetGenericTypeParameter(object? genericContext, int index) => other;

    public DecodedType GetModifiedType(DecodedType modifier, DecodedType unmodifiedType, bool isRequired) => unmodifiedType;

    public DecodedType GetPinnedType(DecodedType elementType) => elementType;

    public DecodedType GetTypeFromSpecification(MetadataReader r, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => other;

    // ICustomAttributeTypeProvider. Scry reads string, bool and System.Type arguments; the member that
    // exists for enum-valued ones never needs to produce anything useful.

    static NamedDecoded systemType = new("System.Type", default, IsDefinition: false);

    public DecodedType GetSystemType() => systemType;

    // A type in the attribute's own assembly is written by its full name; one from anywhere else is
    // assembly-qualified, so the name is cut at the first comma. A nested type's '+' is kept: no type
    // Scry reads by name is nested, so one that is simply matches nothing.
    public DecodedType GetTypeFromSerializedName(string? name)
    {
        if (name is null)
        {
            return new SerializedTypeDecoded(null);
        }

        var comma = name.IndexOf(',');
        if (comma < 0)
        {
            return new SerializedTypeDecoded(name.Trim());
        }

        return new SerializedTypeDecoded(name.Substring(0, comma).Trim());
    }

    public PrimitiveTypeCode GetUnderlyingEnumType(DecodedType type) => PrimitiveTypeCode.Int32;

    // A fixed argument's type comes from the constructor signature, where System.Type is an ordinary
    // type reference. Recognizing it here is what makes the reader parse the argument as a serialized
    // type name; answering false left it parsed as an enum, which is what a typeof(...) would misread as.
    public bool IsSystemType(DecodedType type) =>
        type is NamedDecoded {FullName: "System.Type"};

    static string Combine(string ns, string name)
    {
        if (ns.Length == 0)
        {
            return name;
        }

        return $"{ns}.{name}";
    }
}
