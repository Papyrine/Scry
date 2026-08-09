static class AttributeExtensions
{
    /// <summary>
    /// Whether <paramref name="member"/> carries <typeparamref name="T"/>. Asks for the attribute's
    /// presence rather than constructing it, which is all a marker attribute is ever read for.
    /// </summary>
    /// <remarks>
    /// Answered through <see cref="Attribute.IsDefined(MemberInfo, Type, bool)"/> rather than
    /// <see cref="MemberInfo.IsDefined"/>, which is what makes <paramref name="inherit"/> mean the
    /// same here as it does to <c>GetCustomAttribute</c>: only the former walks an overridden
    /// property's base declarations.
    /// </remarks>
    public static bool HasAttribute<T>(this MemberInfo member, bool inherit = true)
        where T : Attribute =>
        Attribute.IsDefined(member, typeof(T), inherit);
}
