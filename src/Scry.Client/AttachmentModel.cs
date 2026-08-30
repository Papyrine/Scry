/// <summary>
/// The <c>[ScryModel]</c> facts an attachment needs, cached per type. A model carrying no attachment
/// answers null, which is what keeps every query that has none on exactly the path it was on before.
/// </summary>
static class AttachmentModel
{
    static readonly ConcurrentDictionary<Type, ScryModelAttribute?> models = new();

    public static ScryModelAttribute? Of(Type type) =>
        models.GetOrAdd(type, _ => _.GetCustomAttribute<ScryModelAttribute>(inherit: false));

    /// <summary>
    /// The source and key a type's attachments are fetched by. Throws rather than returning null when
    /// the type declares an attachment without the metadata to fetch it: a hand-written model can be
    /// missing it, and silently dropping the handle would fail later as a null reference.
    /// </summary>
    public static (string Source, IReadOnlyList<string> Keys) Fetching(Type type, string member)
    {
        if (Of(type) is not { } model)
        {
            throw new NotSupportedException(
                $"'{type.Name}.{member}' is an attachment, but '{type.Name}' carries no [ScryModel] naming the source and key to fetch it by. A generated model carries one; a hand-written model has to declare it.");
        }

        if (model.Keys.Length == 0)
        {
            throw new NotSupportedException(
                $"'{type.Name}.{member}' is an attachment, but '{type.Name}' declares no keys on its [ScryModel]. An attachment is fetched by its row's key, so the key members have to be named there.");
        }

        return (model.Source, model.Keys);
    }
}