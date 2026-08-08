/// <summary>
/// What an attachment policy type declares about itself, and the cached reflection used to call it.
/// Mirrors <see cref="RowPolicy"/>: the authorized type is read off the policy rather than assumed to
/// be the source's own, since a policy attached to a base is written against that base.
/// </summary>
static class AttachmentPolicy
{
    static readonly ConcurrentDictionary<Type, (Type Entity, MethodInfo Authorize)> resolved = new();

    /// <summary>
    /// The type a policy authorizes — the <c>T</c> of the one <see cref="IAttachmentPolicy{T}"/> it
    /// implements. Anything else is a configuration mistake and throws, which <c>Schema.Build</c>
    /// turns into a startup failure: <c>[AttachmentWith]</c> takes a bare <see cref="Type"/> and
    /// cannot be checked by the compiler.
    /// </summary>
    public static Type EntityType(Type policyType) =>
        Describe(policyType).Entity;

    /// <summary>
    /// Runs the check. The policy is resolved from DI where it was registered and constructed
    /// otherwise, exactly as a row policy is, so one needing services can take them and one needing
    /// nothing can be a bare type.
    /// </summary>
    public static bool Authorize(Type policyType, IServiceProvider services, ScryAttachmentContext context)
    {
        var policy = services.GetService(policyType) ?? Activator.CreateInstance(policyType);
        if (policy is null)
        {
            throw new($"Could not create attachment policy '{policyType.Name}'.");
        }

        return (bool) Describe(policyType).Authorize.Invoke(policy, [context])!;
    }

    static (Type Entity, MethodInfo Authorize) Describe(Type policyType) =>
        resolved.GetOrAdd(
            policyType,
            type =>
            {
                var interfaces = type.GetInterfaces()
                    .Where(_ => _.IsGenericType && _.GetGenericTypeDefinition() == typeof(IAttachmentPolicy<>))
                    .ToList();

                if (interfaces.Count == 0)
                {
                    throw new($"Attachment policy '{type.Name}' does not implement IAttachmentPolicy<T>, so there is nothing for it to authorize.");
                }

                if (interfaces.Count > 1)
                {
                    throw new(
                        $"Attachment policy '{type.Name}' implements IAttachmentPolicy<T> for {string.Join(" and ", interfaces.Select(_ => _.GenericTypeArguments[0].Name))}, so which rows it authorizes is ambiguous. Write one policy type per authorized type.");
                }

                var single = interfaces[0];
                return (single.GenericTypeArguments[0], single.GetMethod(nameof(IAttachmentPolicy<object>.Authorize))!);
            });
}
