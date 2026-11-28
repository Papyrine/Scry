using Microsoft.Extensions.Hosting;

namespace Scry;

public static partial class ScryServiceExtensions
{
    /// <summary>
    /// Writes this server's description to <paramref name="path"/>, resolved against the content root:
    /// the document the explorer's introspection endpoint serves, for a client generated from it at
    /// build time rather than from a running server, such as a TypeScript app's generator or its Vite
    /// plugin.
    /// </summary>
    /// <param name="host">The application, once <c>AddScry</c> has registered the processor.</param>
    /// <param name="path">Where to write it, relative to the content root or absolute.</param>
    /// <param name="always">
    /// Writes it in every environment. Without it the file is written in Development alone, as the
    /// explorer is served: the description is the whole allow-listed surface, so writing it is never
    /// something a production host does by accident.
    /// </param>
    /// <remarks>
    /// Written only where its contents changed, so a host started again over the same model leaves
    /// the file, and whatever watches it, alone. Written whole and then moved into place, so a watcher
    /// never reads half of one.
    /// </remarks>
    public static void WriteScryDescription(this IHost host, string path, bool always = false)
    {
        var services = host.Services;
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (!always &&
            !environment.IsDevelopment())
        {
            return;
        }

        var processor = services.GetRequiredService<ScryProcessor>();

        // What the explorer serves, less what only it knows: the endpoint it sends to, and whether it
        // offers SQL. Neither is part of the surface a client is generated from.
        var bytes = Encoding.UTF8.GetBytes(ScryJson.Serialize(processor.Describe()));
        var full = Path.GetFullPath(path, environment.ContentRootPath);
        if (File.Exists(full) &&
            File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var writing = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(writing, bytes);
            File.Move(writing, full, overwrite: true);
        }
        finally
        {
            File.Delete(writing);
        }
    }
}
