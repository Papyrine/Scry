namespace Scry;

/// <summary>
/// Serves the embedded Blazor WASM explorer assets. The published <c>wwwroot</c> was embedded as
/// manifest resources under the <c>scryui/</c> prefix (see Scry.Server.Explorer.csproj); this maps
/// request paths back to those resources and supplies the content types a Blazor WASM app needs.
/// </summary>
sealed class ExplorerAssets
{
    const string prefix = "scryui/";

    // Every published path with the hash of its contents, written by the embedding target so a
    // changed asset recompiles this assembly. Read here for a second purpose: the hash is the
    // asset's ETag.
    const string stamp = "scryui.stamp";

    static readonly Lazy<ExplorerAssets> lazy = new(() => new());

    public static ExplorerAssets Instance => lazy.Value;

    readonly Assembly assembly = typeof(ExplorerAssets).Assembly;
    readonly Dictionary<string, string> pathToResource = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> pathToTag = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>True once the embedded UI is present (i.e. the package was built with the UI published in).</summary>
    public bool HasAssets => pathToResource.Count > 0;

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
        return reader.ReadToEnd();
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
