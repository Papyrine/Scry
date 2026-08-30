/// <summary>
/// Whether a row type carries an attachment handle anywhere in its shape. Answered from the type
/// alone and cached, so a query whose rows have none skips the attachment machinery entirely rather
/// than translating a second time to discover there was nothing to find.
/// </summary>
static class AttachmentShape
{
    static readonly ConcurrentDictionary<Type, bool> carries = new();

    public static bool Carries(Type type) =>
        carries.GetOrAdd(type, _ => Walk(_, depth: 0));

    static bool Walk(Type type, int depth)
    {
        // A projection nests as deeply as the navigations it descends into, which the server bounds by
        // MaxNavigationDepth. This only has to be no shallower than that bound; a type graph that
        // cycles is what the depth is really guarding against.
        if (depth > 8 ||
            type.IsPrimitive ||
            type == typeof(string))
        {
            return false;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propertyType = property.PropertyType;
            if (propertyType == typeof(ScryAttachment))
            {
                return true;
            }

            // A generated model's navigations are models of their own, and a projection's nested
            // objects are anonymous types — either can be where the handle actually sits.
            if (!propertyType.IsPrimitive &&
                propertyType != typeof(string) &&
                propertyType.IsClass &&
                Walk(propertyType, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
