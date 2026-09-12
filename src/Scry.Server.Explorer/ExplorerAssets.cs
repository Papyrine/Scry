namespace Scry;

/// <summary>
/// Serves the embedded Blazor WASM explorer assets. The published <c>wwwroot</c> was embedded as
/// manifest resources under the <c>scryui/</c> prefix (see Scry.Server.Explorer.csproj); this maps
/// request paths back to those resources and supplies the content types a Blazor WASM app needs.
/// </summary>
sealed partial class ExplorerAssets
{
    const string prefix = "scryui/";

    // Every published path with the hash of its contents, written by the embedding target so a
    // changed asset recompiles this assembly. Read here for a second purpose: the hash is the
    // asset's ETag.
    const string stamp = "scryui.stamp";

    static Lazy<ExplorerAssets> lazy = new(() => new());

    public static ExplorerAssets Instance => lazy.Value;

    Assembly assembly = typeof(ExplorerAssets).Assembly;
    Dictionary<string, string> pathToResource = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> pathToTag = new(StringComparer.OrdinalIgnoreCase);

    ExplorerAssets()
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pathToResource[Normalize(name[prefix.Length..])] = name;
        }

        ReadStamp();
    }

    // One line per asset: the path, a space, the hash. The path is written by MSBuild, so its
    // separators are the build machine's.
    void ReadStamp()
    {
        using var stream = assembly.GetManifestResourceStream(stamp);
        if (stream is null)
        {
            return;
        }

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var split = line.LastIndexOf(' ');
            if (split <= 0)
            {
                continue;
            }

            pathToTag[Normalize(line[..split])] = $"\"{line[(split + 1)..]}\"";
        }
    }

    /// <summary>
    /// True once the embedded UI is present. Asks for the host page rather than counting resources:
    /// it is the one asset a mapping cannot start without, and the only one whose absence would
    /// otherwise surface as an exception mid-request.
    /// </summary>
    public bool HasAssets => pathToResource.ContainsKey("index.html");

    /// <summary>
    /// The <c>'sha256-…'</c> source expressions for <paramref name="html"/>'s inline scripts, in
    /// document order. What a <c>script-src</c> lists to allow exactly those scripts and no other
    /// inline one.
    /// </summary>
    /// <remarks>
    /// Takes the page rather than reading it, so the caller hashes the text it is about to serve: a
    /// hash read off the embedded page instead would be right only for as long as no serve-time
    /// rewrite reached inside a script, and wrong silently — the browser refuses the script and the
    /// explorer never boots. Hashes at all rather than a nonce because the page then stays the same
    /// bytes on every serve, which is what its ETag needs; a nonce would be minted per response and
    /// every revalidation would be a download.
    /// </remarks>
    public static IReadOnlyList<string> InlineScriptHashes(string html)
    {
        var hashes = new List<string>();
        foreach (Match match in InlineScript().Matches(html))
        {
            // The browser hashes exactly the element's text, whitespace included, so the capture keeps
            // it whole — and reads it off the same normalized text the page is served as.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(match.Groups[1].Value));
            hashes.Add($"'sha256-{Convert.ToBase64String(hash)}'");
        }

        return hashes;
    }

    // A script element carrying no src: the ones whose text the page itself holds.
    [GeneratedRegex(@"<script(?![^>]*\ssrc\s*=)[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    /// <summary>
    /// Opens an embedded asset. The tag is the entity tag its content hash makes, quoted as the
    /// header wants it, or null for an asset the stamp does not list.
    /// </summary>
    public bool TryOpen(string path, out Stream stream, out string contentType, out string? tag)
    {
        if (pathToResource.TryGetValue(Normalize(path), out var name))
        {
            stream = assembly.GetManifestResourceStream(name)!;
            contentType = ContentType(path);
            tag = pathToTag.GetValueOrDefault(Normalize(path));
            return true;
        }

        stream = Stream.Null;
        contentType = "application/octet-stream";
        tag = null;
        return false;
    }

    public string ReadText(string path)
    {
        using var stream = assembly.GetManifestResourceStream(pathToResource[Normalize(path)])!;
        using var reader = new StreamReader(stream);

        // Line endings as the HTML parser normalizes them, so a hash computed over this text is the
        // hash a browser computes over the script it parsed out of the same bytes.
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".js" => "text/javascript",
            ".mjs" => "text/javascript",
            ".css" => "text/css",
            ".wasm" => "application/wasm",
            ".json" => "application/json",
            ".ico" => "image/x-icon",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            // .dll/.dat/.blat/.pdb and anything else: opaque binary the runtime fetches by name.
            _ => "application/octet-stream"
        };
}
