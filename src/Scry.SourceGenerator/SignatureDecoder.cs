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
/// Decodes property type signatures into <see cref="DecodedType"/> using
/// <see cref="System.Reflection.Metadata"/>, recognizing only the shapes Scry cares about. Also
/// serves as the custom-attribute type provider, which is only ever asked to decode the
/// <c>string Name</c> named argument on the queryable attributes and the message on <c>[Obsolete]</c>.
/// </summary>
sealed class SignatureDecoder :
    ISignatureTypeProvider<DecodedType, object?>,
    ICustomAttributeTypeProvider<DecodedType>
{
    static readonly OtherDecoded other = new();
    static readonly BytesDecoded bytes = new();

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
    static readonly HashSet<string> collectionTypes =
    [
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.Collections.ObjectModel.ObservableCollection`1"
    ];

    public DecodedType GetSZArrayType(DecodedType elementType) =>
        elementType switch
        {
            PrimitiveDecoded { Code: PrimitiveTypeCode.Byte } => bytes,
            NamedDecoded => new CollectionDecoded(elementType),
            _ => other
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

    // ICustomAttributeTypeProvider. Scry only reads string and bool arguments, so the members that
    // exist for System.Type and enum-valued arguments never need to produce anything useful.

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
