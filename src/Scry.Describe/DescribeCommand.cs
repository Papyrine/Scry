/// <summary>
/// <c>Scry.Describe --model &lt;dll&gt; --output &lt;file&gt;</c>: writes the description of a model
/// to a file, from its DLL, for a client generated at build time.
/// </summary>
static class DescribeCommand
{
    // ScryOptions' own defaults, which the server describes where a host sets neither. Scry.Server is
    // not referenced, so they are spelled here; ModelDescriptionTests holds them to the server's.
    internal const int DefaultMaxPageSize = 1000;
    internal const int DefaultQueryUrlLimit = QueryUrl.MaxLength;

    const string usage =
        """
        Usage: Scry.Describe --model <dll> --output <file> [--max-page-size <n>] [--query-url-limit <n>]

          --model            The model assembly the server's [Queryable] types are declared in.
          --output           Where to write the description.
          --max-page-size    ScryOptions.MaxPageSize, where the server sets it. Default: 1000.
          --query-url-limit  ScryOptions.QueryUrlLimit, where the server sets it. Default: 4096.
        """;

    static int Main(string[] args) =>
        Run(args, Console.Out, Console.Error);

    internal static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        string? model = null;
        string? path = null;
        var maxPageSize = DefaultMaxPageSize;
        var queryUrlLimit = DefaultQueryUrlLimit;
        for (var index = 0; index < args.Count; index++)
        {
            var value = index + 1 < args.Count ? args[index + 1] : null;
            switch (args[index])
            {
                case "--model" when value is not null:
                    model = value;
                    index++;
                    break;
                case "--output" when value is not null:
                    path = value;
                    index++;
                    break;
                case "--max-page-size" when int.TryParse(value, out var size):
                    maxPageSize = size;
                    index++;
                    break;
                case "--query-url-limit" when int.TryParse(value, out var limit):
                    queryUrlLimit = limit;
                    index++;
                    break;
                default:
                    error.WriteLine(usage);
                    return 2;
            }
        }

        if (model is null ||
            path is null)
        {
            error.WriteLine(usage);
            return 2;
        }

        // The reader reads a missing file as an empty model, as the generator wants of an empty
        // ScryModelDll. A description of nothing would generate a client of nothing, so it is refused.
        var full = Path.GetFullPath(model);
        if (!File.Exists(full))
        {
            error.WriteLine($"{model} does not exist: build the model first.");
            return 1;
        }

        // The reader insists on an absolute path, since inside the compiler a relative one would be read
        // against a working directory that is not the project's. Here it is the caller's.
        var extract = MetadataModelReader.Read(full);
        if (extract.Sources.Length == 0 &&
            extract.Commands.Length == 0 &&
            extract.Error is null)
        {
            error.WriteLine($"{model} declares nothing a client could use: no [Queryable] type and no [Command].");
            return 1;
        }

        if (ModelDescriber.Problems(extract) is {Count: > 0} problems)
        {
            error.WriteLine($"{model} cannot be described:");
            foreach (var problem in problems)
            {
                error.WriteLine($"  {problem}");
            }

            return 1;
        }

        var description = ModelDescriber.Describe(extract, maxPageSize, queryUrlLimit);
        var bytes = Encoding.UTF8.GetBytes(ScryJson.Serialize(description));
        if (!Write(Path.GetFullPath(path), bytes))
        {
            output.WriteLine($"{path} is already the description of {model} (schema stamp {description.SchemaStamp}).");
            return 0;
        }

        output.WriteLine($"Wrote {path} from {model} (schema stamp {description.SchemaStamp}).");
        return 0;
    }

    // Only where the bytes changed, so a watcher on the file is told only of a change, and whole and then
    // moved into place, so it never reads half of one. True where it wrote.
    static bool Write(string path, byte[] bytes)
    {
        if (File.Exists(path) &&
            File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var writing = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(writing, bytes);
            File.Move(writing, path, overwrite: true);
        }
        finally
        {
            File.Delete(writing);
        }

        return true;
    }
}
