/// <summary>
/// What a row policy type declares about itself. A policy is attached to a type but written against
/// the type it filters, and the two differ whenever one is inherited, so the filtered type is read off
/// the policy rather than assumed to be the source's own.
/// </summary>
static class RowPolicy
{
    /// <summary>
    /// The type a policy filters — the <c>T</c> of the one <see cref="IReturnablePolicy{T}"/> it
    /// implements, and so the type a query is widened to before the policy runs. Anything else is a
    /// configuration mistake and throws, which <c>Schema.Build</c> turns into a startup failure:
    /// <c>[ReturnableWith]</c> takes a bare <see cref="Type"/> and cannot be checked by the compiler.
    /// </summary>
    public static Type EntityType(Type policyType)
    {
        var filtered = policyType.GetInterfaces()
            .Where(_ => _.IsGenericType &&
                        _.GetGenericTypeDefinition() == typeof(IReturnablePolicy<>))
            .Select(_ => _.GenericTypeArguments[0])
            .ToList();

        if (filtered.Count == 1)
        {
            return filtered[0];
        }

        if (filtered.Count == 0)
        {
            throw new($"Row policy '{policyType.Name}' does not implement IReturnablePolicy<T>, so there is nothing for it to filter.");
        }

        throw new(
            $"Row policy '{policyType.Name}' implements IReturnablePolicy<T> for {string.Join(" and ", filtered.Select(_ => _.Name))}, so which rows it filters is ambiguous. Write one policy type per filtered type.");
    }
}
