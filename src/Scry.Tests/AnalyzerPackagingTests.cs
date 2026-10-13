using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>
/// Cover for which compilers can load the generator builds the <c>Scry.Client</c> package ships.
/// </summary>
/// <remarks>
/// Each build sits in an <c>analyzers/dotnet/roslynX.Y/cs</c> folder, and the SDK hands a compiler
/// the newest folder that is not newer than the compiler itself. The compiler then refuses an
/// analyzer that references a newer Microsoft.CodeAnalysis than its own, with a warning (CS9057)
/// rather than an error: the generator is skipped and the consumer is left without a
/// <c>Scry.Generated</c> namespace. So a folder name is a promise about the references of the
/// assembly inside it. Nothing else can notice that promise being broken, because every project in
/// this repository is compiled by the current SDK, which satisfies any reference, and takes the
/// generator as a project rather than from the package.
/// </remarks>
public class AnalyzerPackagingTests
{
    // Scry.Client targets net10.0 only, and the first .NET 10 SDK ships Roslyn 5.0, so no consumer
    // has an older compiler than this. Raising it drops SDK feature bands that worked before.
    static readonly Version oldestCompiler = new(5, 0);

    record CompilerReference(string Assembly, Version Folder, string Name, Version Version);

    record Package(List<Version> Folders, List<CompilerReference> References);

    static readonly Lazy<Package> package = new(ReadPackage);

    // The nupkg the build wrote, which is the file that gets published. Only a Release build packs
    // (ProjectDefaults), so under Debug there is either nothing to read or a package left behind by
    // an earlier build of other sources.
    static Package Read()
    {
#if DEBUG
        Skip.Test("A Debug build does not pack Scry.Client. Run the suite with -c Release.");
#endif
        return package.Value;
    }

    static string PackagePath([CallerFilePath] string sourceFile = "")
    {
        // 0.1.0-beta.13+{commit}: the package is named for the part before the build metadata.
        var informational = typeof(ScryClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var version = informational.Split('+')[0];
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(root, "nugets", $"Scry.Client.{version}.nupkg");
    }

    static Package ReadPackage()
    {
        var folders = new List<Version>();
        var references = new List<CompilerReference>();
        using var archive = ZipFile.OpenRead(PackagePath());
        foreach (var entry in archive.Entries)
        {
            // analyzers/dotnet/roslyn5.0/cs/Scry.SourceGenerator.dll
            if (entry.FullName.Split('/') is not ["analyzers", "dotnet", var folder, .., var file] ||
                !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A build outside a roslynX.Y folder is handed to every compiler, so it promises the
            // oldest of them all. Counting it as version 0.0 makes any compiler reference too new.
            var version = new Version(0, 0);
            if (folder.StartsWith("roslyn", StringComparison.Ordinal))
            {
                version = Version.Parse(folder["roslyn".Length..]);
            }

            if (!folders.Contains(version))
            {
                folders.Add(version);
            }

            ReadReferences(entry, version, references);
        }

        return new(folders, references);
    }

    // Every assembly in the folder, not only the generator: anything shipped beside it loads into
    // the same compiler, and one of them picking up a compiler reference would be refused the same way.
    static void ReadReferences(ZipArchiveEntry entry, Version folder, List<CompilerReference> references)
    {
        // A zip entry cannot seek, which reading the metadata needs.
        using var stream = new MemoryStream();
        using (var compressed = entry.Open())
        {
            compressed.CopyTo(stream);
        }

        stream.Position = 0;
        using var portable = new PEReader(stream);
        var metadata = portable.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            if (name == "Microsoft.CodeAnalysis" ||
                name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal))
            {
                references.Add(new(entry.FullName, folder, name, reference.Version));
            }
        }
    }

    [Test]
    public async Task NoBuildReferencesACompilerNewerThanItsFolder()
    {
        var newer = new List<string>();
        foreach (var reference in Read().References)
        {
            // A compiler's assembly version carries major and minor only, as does a folder name.
            var referenced = new Version(reference.Version.Major, reference.Version.Minor);
            if (referenced > reference.Folder)
            {
                newer.Add($"{reference.Assembly} references {reference.Name} {reference.Version}");
            }
        }

        await Assert.That(newer).IsEmpty().Because("A compiler as old as the folder name skips these. Pin the generator's Microsoft.CodeAnalysis.CSharp for that target framework to the folder's version.");
    }

    // Guards the test above against passing on nothing: a folder that was renamed, emptied, or
    // filled from the wrong build output would otherwise have no references to compare.
    [Test]
    public async Task EveryFolderHoldsABuildOfTheGenerator()
    {
        var read = Read();
        await Assert.That(read.Folders).IsNotEmpty();
        foreach (var folder in read.Folders)
        {
            var generator = read.References.Exists(_ =>
                _.Folder == folder &&
                _.Name == "Microsoft.CodeAnalysis" &&
                _.Assembly.EndsWith("/Scry.SourceGenerator.dll", StringComparison.Ordinal));
            await Assert.That(generator).IsTrue().Because($"The folder for Roslyn {folder} holds no Scry.SourceGenerator.dll");
        }
    }

    // Renaming a folder upward satisfies the comparison above just as well as lowering a reference
    // does, and silently strands every compiler older than the new name.
    [Test]
    public async Task TheOldestCompilerHasABuild()
    {
        var supported = Read().Folders.Exists(_ => _ <= oldestCompiler);
        await Assert.That(supported).IsTrue().Because($"No folder is old enough for a Roslyn {oldestCompiler} compiler");
    }
}
