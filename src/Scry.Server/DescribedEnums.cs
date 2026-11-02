/// <summary>
/// The enums the surface reaches, by the name clients know them by. The generated enums share one
/// namespace, so two of the model's with one simple name would be described as one, and a client
/// would read one's values by the other's names: such a model is refused.
/// </summary>
/// <remarks>
/// Must stay in lockstep with the generator's <c>CollectEnum</c>, which refuses the same model.
/// </remarks>
sealed class DescribedEnums() :
    Dictionary<string, ScryEnumInfo>(StringComparer.Ordinal)
{
    Dictionary<string, Type> types = new(StringComparer.Ordinal);

    public void Add(Type type, Func<Type, ScryEnumInfo> describe)
    {
        // Emitted under the type's own name, which the generator refuses as SCRY019 where C# cannot
        // write it. Refused here too, so the mistake surfaces at startup whichever side is built first.
        if (!CSharpIdentifier.IsValid(type.Name))
        {
            throw new($"The enum '{type.FullName}' is reachable from the queryable surface, and its name cannot be written as a C# type name, so no client can be generated for it. Rename it to a plain identifier that is not a reserved keyword.");
        }

        if (types.TryGetValue(type.Name, out var existing))
        {
            if (existing != type)
            {
                throw new($"The enums '{existing.FullName}' and '{type.FullName}' are both reachable from the queryable surface and share the name '{type.Name}'. Rename one.");
            }

            return;
        }

        types.Add(type.Name, type);
        Add(type.Name, describe(type));
    }
}
