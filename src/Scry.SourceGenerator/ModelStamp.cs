// The schema stamp a generated client carries, and the display its members are stamped with, apart
// from the generator itself: nothing here touches Roslyn, so a tool reading a model as metadata (the
// build-time description, Scry.Describe) stamps it without loading a compiler. Mirrors Schema.
static class ModelStamp
{
    /// <summary>
    /// The type the generated member is declared as. Everything but an attachment is spelled as the
    /// model spelled it; an attachment becomes the handle, which is the whole point of the attribute
    /// and the reason — unlike <c>[BinaryTransfer]</c> — that it moves the schema stamp.
    /// </summary>
    /// <remarks>Mirrored by <c>Schema.DescribeMember</c>, which the schema stamp requires to agree.</remarks>
    internal static string Display(PropertyInfo property)
    {
        if (property.IsAttachment)
        {
            return "global::Scry.ScryAttachment";
        }

        return property.TypeDisplay;
    }

    // Mirrors Schema.ComputeStamp on the server: same canonical inputs into the shared SchemaStamp,
    // so a client generated from the same surface carries the same stamp the server computes.
    // Deprecation is deliberately absent: marking something [Obsolete] leaves the queryable surface
    // exactly as it was, and folding it in would report every deployed client as stale for what is
    // only a note to whoever next rebuilds one.
    internal static string Compute(ModelExtract extract)
    {
        var sources = extract.Sources
            .Where(_ => _.Kind != SourceKind.Complex)
            .Select(_ => (_.SourceName, _.Kind.ToString(), _.ModelName))
            .ToList();
        var types = extract.Sources
            .Select(_ => (_.ModelName, _.BaseModelName, StampMembers(_)))
            .ToList();
        var enums = extract.Enums
            .Select(_ => (
                _.Name,
                _.Underlying,
                _.IsFlags,
                _.Members.ToList().Zip(_.Values.ToList(), (name, value) => (name, value)).ToList()))
            .ToList();
        var commands = extract.Commands
            .Select(_ => (_.Name, _.Target, StampCommandMembers(_)))
            .ToList();
        var results = extract.Results
            .Select(_ => (_.Name, _.Properties.Select(property => (property.Name, property.TypeDisplay)).ToList()))
            .ToList();
        return SchemaStamp.Compute(sources, types, enums, commands, results);
    }

    /// <summary>
    /// The members a command contributes to the stamp: its payload, plus synthetic members naming the
    /// payload properties its target's key is bound to and the class it answers with. Mirrored by
    /// <c>Schema.StampCommandMembers</c>, byte for byte.
    /// </summary>
    static List<(string, string)> StampCommandMembers(CommandInfo command)
    {
        var members = command.Properties
            .Select(_ => (_.Name, _.TypeDisplay))
            .ToList();
        if (command.Keys.Length > 0)
        {
            members.Add(("~keys", string.Join(" ", command.Keys)));
        }

        if (command.ResultName is { } result)
        {
            members.Add(("~result", result));
        }

        return members;
    }

    /// <summary>
    /// The members a type contributes to the stamp: its own, plus — for one carrying an attachment —
    /// a synthetic member naming the key that attachment is fetched by. The key is part of the
    /// client-visible contract only once something is fetched by it, so folding it in everywhere
    /// would report every deployed client as stale for a surface that did not change.
    /// </summary>
    /// <remarks>
    /// <c>~</c> cannot begin a C# identifier, so the synthetic name can never collide with a real
    /// member's. Mirrored by <c>Schema.StampMembers</c>.
    /// </remarks>
    static List<(string, string)> StampMembers(SourceInfo source)
    {
        var members = source.Properties
            .Select(_ => (_.Name, Display(_)))
            .ToList();
        if (source.Keys.Length > 0)
        {
            members.Add(("~keys", string.Join(' ', source.Keys)));
        }

        if (Sensitivity(source) is { Length: > 0 } sensitive)
        {
            members.Add(("~sensitive", sensitive));
        }

        return members;
    }

    /// <summary>
    /// The sensitivity line a type contributes to the stamp: the members it marks, and <c>*</c> where
    /// the type itself is marked. Present only where something is, so a model that marks nothing hashes
    /// exactly as it did before <c>[Sensitive]</c> existed.
    /// </summary>
    /// <remarks>
    /// Hashed — unlike <c>[Obsolete]</c> — because it changes what an already-deployed client may do
    /// rather than only what it should be told. A client generated before a member was marked keeps
    /// asking in URLs and is refused; moving the stamp is what turns that into a reported staleness
    /// with a regenerate to fix it. <c>*</c> is not a member name, so it cannot collide with one.
    /// </remarks>
    static string Sensitivity(SourceInfo source)
    {
        // Sorted, because the joined value is hashed whole and the two sides meet the members in
        // different orders: this one in declaration order, the server in the name order it describes
        // them in. Mirrored by Schema.Sensitivity.
        var names = source.Properties
            .Where(_ => _.IsSensitive)
            .Select(_ => _.Name)
            .OrderBy(_ => _, StringComparer.Ordinal)
            .ToList();
        if (source.IsSensitive)
        {
            names.Insert(0, "*");
        }

        return string.Join(' ', names);
    }
}
