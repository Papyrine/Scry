/// <param name="Target">
/// The path to the attachment member in the projected object — one segment for a flat projection, two
/// for one nested into a navigation.
/// </param>
/// <param name="Root">The name of the source the attachment is fetched from.</param>
/// <param name="Member">The attachment member on that source's row.</param>
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