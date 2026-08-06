/// <summary>
/// Where a result's attachment handles come from: for each one, which member of the projected object
/// it fills, which source and member it fetches, and where in that same object its row's key values
/// landed. Built while the query is translated, since that is the only point at which the projection
/// and the model behind it are both in view.
/// </summary>
sealed record AttachmentPlan(IReadOnlyList<AttachmentBinding> Bindings);

/// <param name="Target">
/// The path to the attachment member in the projected object — one segment for a flat projection, two
/// for one nested into a navigation.
/// </param>
/// <param name="KeySources">
/// Where each of the row's key values sits in that same object, in the order the wire carries them.
/// Read off the materialized row rather than the JSON, so a key already parsed into its CLR type is
/// re-tagged exactly as a constant of that type would be.
/// </param>
sealed record AttachmentBinding(
    IReadOnlyList<string> Target,
    string Root,
    string Member,
    IReadOnlyList<IReadOnlyList<string>> KeySources);

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
