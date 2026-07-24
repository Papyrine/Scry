/// <summary>A minimal decoded representation of a property's type, enough to classify and emit it.</summary>
abstract record DecodedType;

sealed record PrimitiveDecoded(PrimitiveTypeCode Code) :
    DecodedType;

sealed record NamedDecoded(string FullName, EntityHandle Handle, bool IsDefinition) :
    DecodedType;

sealed record NullableDecoded(DecodedType Inner) :
    DecodedType;

/// <summary>Anything Scry does not expose (arrays, collections, generics other than Nullable).</summary>
sealed record OtherDecoded :
    DecodedType;

/// <summary>
/// Decodes property type signatures into <see cref="DecodedType"/> using
/// <see cref="System.Reflection.Metadata"/>, recognizing only the shapes Scry cares about. Also
/// serves as the custom-attribute type provider, which is only ever asked to decode the
/// <c>string Name</c> named argument on the queryable attributes.
/// </summary>
sealed class SignatureDecoder :
    ISignatureTypeProvider<DecodedType, object?>,
    ICustomAttributeTypeProvider<DecodedType>
{
    static readonly OtherDecoded other = new();

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
        if (genericType is NamedDecoded
            {
                FullName: "System.Nullable`1"
            } &&
            typeArguments.Length == 1)
        {
            return new NullableDecoded(typeArguments[0]);
        }

        return other;
    }

    public DecodedType GetSZArrayType(DecodedType elementType) => other;

    public DecodedType GetArrayType(DecodedType elementType, ArrayShape shape) => other;

    public DecodedType GetByReferenceType(DecodedType elementType) => other;

    public DecodedType GetPointerType(DecodedType elementType) => other;

    public DecodedType GetFunctionPointerType(MethodSignature<DecodedType> signature) => other;

    public DecodedType GetGenericMethodParameter(object? genericContext, int index) => other;

    public DecodedType GetGenericTypeParameter(object? genericContext, int index) => other;

    public DecodedType GetModifiedType(DecodedType modifier, DecodedType unmodifiedType, bool isRequired) => unmodifiedType;

    public DecodedType GetPinnedType(DecodedType elementType) => elementType;

    public DecodedType GetTypeFromSpecification(MetadataReader r, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => other;

    // ICustomAttributeTypeProvider. Scry only reads a string-valued named argument, so the members
    // that exist for System.Type and enum-valued arguments never need to produce anything useful.

    public DecodedType GetSystemType() => other;

    public DecodedType GetTypeFromSerializedName(string name) => other;

    public PrimitiveTypeCode GetUnderlyingEnumType(DecodedType type) => PrimitiveTypeCode.Int32;

    public bool IsSystemType(DecodedType type) => false;

    static string Combine(string ns, string name)
    {
        if (ns.Length == 0)
        {
            return name;
        }

        return $"{ns}.{name}";
    }
}
