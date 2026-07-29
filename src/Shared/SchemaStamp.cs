// Linked into both Scry.Server (net10.0) and Scry.SourceGenerator, so the two sides compute the stamp
// from one implementation. The generator also multi-targets netstandard2.0 — the target Roslyn loads
// as an analyzer — so this file must compile there too. SHA256.HashData is .NET 5+; Polyfill (a
// source-only package, so nothing extra to bundle into the analyzer) supplies it on netstandard2.0,
// which is what lets one path serve every target while satisfying CA1850 on the net10 ones.

/// <summary>
/// Computes the schema stamp: a truncated SHA-256 over a canonical description of the queryable
/// surface (sources, query-model types with their members, enums). The generator computes it from
/// metadata and bakes it into the generated client; the server computes it from reflection at startup.
/// Equal surfaces yield equal stamps, so a mismatch identifies a client generated against a different
/// model. Every list is sorted ordinal so metadata order and reflection order cannot diverge.
/// </summary>
static class SchemaStamp
{
    /// <summary>
    /// Bytes of the digest kept, base64url-encoded into the 16-character stamp. The stamp is a
    /// fingerprint, not a security boundary: nothing trusts it (every request is re-validated against
    /// the real schema regardless), and it is only ever compared pairwise — one client's against one
    /// server's — so the birthday bound does not apply and a collision costs at most a missed reload
    /// prompt. 96 bits puts that at roughly 1 in 10^29 per rename. 12 divides by 3, so the base64
    /// encoding needs no padding.
    /// </summary>
    const int stampBytes = 12;

    public static string Compute(
        List<(string Name, string Kind, string Model)> sources,
        List<(string Model, List<(string Name, string Type)> Members)> types,
        List<(string Name, List<string> Members)> enums)
    {
        var builder = new StringBuilder();
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

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());

        return Encode(SHA256.HashData(bytes));
    }

    // Base64url (RFC 4648 §5) over the leading StampBytes: the stamp travels in a JSON body, an HTTP
    // header, and a generated C# constant, and '-' and '_' are safe in all three where '+' and '/'
    // are not. No '=' padding to strip, since StampBytes divides by 3.
    static string Encode(byte[] hash)
    {
        var truncated = new byte[stampBytes];
        Array.Copy(hash, truncated, stampBytes);

        return Convert.ToBase64String(truncated)
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
