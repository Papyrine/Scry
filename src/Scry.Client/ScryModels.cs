/// <summary>
/// The <c>[ScryModel]</c> a generated type carries, read once per type: the source it stands for,
/// the members its default projection names, and what an attachment on it needs. Read per send
/// otherwise, and reflection over attributes is not cheap on the interpreter the browser runs.
/// </summary>
static class ScryModels
{
    static readonly ConcurrentDictionary<Type, ScryModelAttribute?> models = new();

    /// <summary>
    /// The attribute, or null for a hand-built model that carries none — which is what keeps every
    /// query over one on exactly the path it was on before.
    /// </summary>
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