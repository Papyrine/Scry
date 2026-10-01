using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A model compiled with Roslyn for one test. A shape that refuses to start cannot live in this
/// assembly — the schema reads the whole assembly it is given, so one bad type would fail every
/// fixture — and a model split across assemblies needs assemblies of its own to split it across.
/// </summary>
/// <remarks>
/// The test assembly is referenced too, so a model can derive from what <c>ForeignBases</c> declares:
/// a base the model does not own, as a library's would be.
/// </remarks>
static class CompiledModel
{
    const string usings =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Microsoft.EntityFrameworkCore;
        using Scry;

        """;

    static List<MetadataReference> references = References();

    /// <summary>
    /// Compiles <paramref name="source"/> and loads it. The image comes back too, for the generator,
    /// which reads a model from disk rather than from a loaded assembly.
    /// </summary>
    public static (Assembly Assembly, byte[] Image) Compile(string source)
    {
        var compilation = CSharpCompilation.Create(
            $"Model{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(usings + source)],
            references,
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        var errors = emitted.Diagnostics
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count > 0)
        {
            throw new($"The model does not compile:\n{string.Join("\n", errors)}");
        }

        var image = stream.ToArray();
        return (System.Reflection.Assembly.Load(image), image);
    }

    static List<MetadataReference> References()
    {
        var trusted = (string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var list = trusted
            .Split(Path.PathSeparator)
            .Where(_ => _.Length > 0)
            .Select(MetadataReference (_) => MetadataReference.CreateFromFile(_))
            .ToList();
        list.Add(MetadataReference.CreateFromFile(typeof(QueryableAttribute).Assembly.Location));
        list.Add(MetadataReference.CreateFromFile(typeof(ScryProcessor).Assembly.Location));
        list.Add(MetadataReference.CreateFromFile(typeof(DbContext).Assembly.Location));
        list.Add(MetadataReference.CreateFromFile(typeof(CompiledModel).Assembly.Location));
        return list;
    }
}
