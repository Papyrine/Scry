using System.Reflection;

namespace Scry;

/// <summary>
/// Serves the embedded Blazor WASM explorer assets. The published <c>wwwroot</c> was embedded as
/// manifest resources under the <c>scryui/</c> prefix (see Scry.Server.Explorer.csproj); this maps
/// request paths back to those resources and supplies the content types a Blazor WASM app needs.
/// </summary>
sealed class ExplorerAssets
{
    const string Prefix = "scryui/";

    static readonly Lazy<ExplorerAssets> lazy = new(() => new ExplorerAssets());

    public static ExplorerAssets Instance => lazy.Value;

    readonly Assembly assembly = typeof(ExplorerAssets).Assembly;
    readonly Dictionary<string, string> pathToResource = new(StringComparer.OrdinalIgnoreCase);

    ExplorerAssets()
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pathToResource[Normalize(name[Prefix.Length..])] = name;
        }
    }

    /// <summary>True once the embedded UI is present (i.e. the package was built with the UI published in).</summary>
    public bool HasAssets => pathToResource.Count > 0;

    public bool TryOpen(string path, out Stream stream, out string contentType)
    {
        if (pathToResource.TryGetValue(Normalize(path), out var name))
        {
            stream = assembly.GetManifestResourceStream(name)!;
            contentType = ContentType(path);
            return true;
        }

        stream = Stream.Null;
        contentType = "application/octet-stream";
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
