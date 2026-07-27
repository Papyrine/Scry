// Linked into both Scry.SourceGenerator (netstandard2.0) and Scry.Server, so the two sides compute
// the stamp from one implementation. BCL references are fully qualified because the two projects
// have different global usings.

/// <summary>
/// Computes the schema stamp: a SHA-256 hash over a canonical description of the queryable surface
/// (sources, query-model types with their members, enums). The generator computes it from metadata
/// and bakes it into the generated client; the server computes it from reflection at startup. Equal
/// surfaces yield equal stamps, so a mismatch identifies a client generated against a different
/// model. Every list is sorted ordinal so metadata order and reflection order cannot diverge.
/// </summary>
static class SchemaStamp
{
    public static string Compute(
        System.Collections.Generic.List<(string Name, string Kind, string Model)> sources,
        System.Collections.Generic.List<(string Model, System.Collections.Generic.List<(string Name, string Type)> Members)> types,
        System.Collections.Generic.List<(string Name, System.Collections.Generic.List<string> Members)> enums)
    {
        var builder = new System.Text.StringBuilder();
        // Versions the canonical form itself, so a future change to what is hashed cannot silently
        // collide with stamps produced by the old form.
        builder.Append("scry-schema-v1\n");

        sources.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        foreach (var (name, kind, model) in sources)
        {
            builder.Append($"source {name} {kind} {model}\n");
        }

        types.Sort((left, right) => string.CompareOrdinal(left.Model, right.Model));
        foreach (var (model, members) in types)
        {
            builder.Append($"type {model}\n");
            members.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            foreach (var (name, type) in members)
            {
                builder.Append($"  {name} {type}\n");
            }
        }

        enums.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        foreach (var (name, members) in enums)
        {
            builder.Append($"enum {name}\n");
            members.Sort(string.CompareOrdinal);
            foreach (var member in members)
            {
                builder.Append($"  {member}\n");
            }
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(builder.ToString());
#if NETSTANDARD2_0
        byte[] hash;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            hash = sha.ComputeHash(bytes);
        }
#else
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
#endif

        var hex = new System.Text.StringBuilder(hash.Length * 2);
        foreach (var value in hash)
        {
            hex.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }
}
