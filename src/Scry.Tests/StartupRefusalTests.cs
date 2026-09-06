using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The model shapes <c>Schema.Build</c> refuses, each given a test. A shape that refuses to start
/// cannot live in this assembly — the schema scans the whole assembly of the context it is built
/// for, so one bad type would fail every fixture — so each case compiles its own model with Roslyn
/// and builds the schema over that. Pinned by message, since the message is the fix a host is told.
/// </summary>
[TestFixture]
public class StartupRefusalTests
{
    [TestCase(
        "[Queryable] public class A { public int Id { get; set; } [Attachment] public string Doc { get; set; } = \"\"; }",
        "[Attachment]",
        TestName = "an attachment that is not a byte array")]
    [TestCase(
        "[Queryable] public class A { public int Id { get; set; } [QueryIgnore] [Attachment] public byte[]? Doc { get; set; } }",
        "not exposed to clients",
        TestName = "an attachment on a hidden member")]
    [TestCase(
        "[Queryable] public class A { public int Id { get; set; } [Attachment] [BinaryTransfer] public byte[]? Doc { get; set; } }",
        "carries both [Attachment] and [BinaryTransfer]",
        TestName = "an attachment that is also a binary transfer")]
    [TestCase(
        "[QueryableComplex] public class C { [Attachment] public byte[]? Doc { get; set; } } [Queryable] public class A { public int Id { get; set; } public C Part { get; set; } = new(); }",
        "[Attachment]",
        TestName = "an attachment on a complex type")]
    [TestCase(
        "[Queryable] [AttachmentWith(typeof(P))] public class A { public int Id { get; set; } [Attachment(ContentType = \"nope\")] public byte[]? Doc { get; set; } } public sealed class P : IAttachmentPolicy<A> { public bool Authorize(ScryAttachmentContext c) => true; }",
        "not a media type",
        TestName = "a declared content type that is not a media type")]
    [TestCase(
        "[Queryable] public class A { public int Id { get; set; } [Attachment] public byte[]? Doc { get; set; } }",
        "attachment",
        TestName = "an attachment with no policy to authorize it")]
    [TestCase(
        "[Queryable(Name = \"Same\")] public class A { public int Id { get; set; } } [Queryable(Name = \"Same\")] public class B { public int Id { get; set; } }",
        "Duplicate queryable source name 'Same'",
        TestName = "two sources with one name")]
    [TestCase(
        "[Queryable(Name = \"not valid\")] public class A { public int Id { get; set; } }",
        "not valid",
        TestName = "a source name that is not an identifier")]
    [TestCase(
        "[Queryable] [ReturnableWith(typeof(P))] public class A { public int Id { get; set; } } [Queryable] public class B { public int Id { get; set; } } public sealed class P : IReturnablePolicy<A>, IReturnablePolicy<B> { public IQueryable<A> Filter(IQueryable<A> s, ScryPolicyContext c) => s; public IQueryable<B> Filter(IQueryable<B> s, ScryPolicyContext c) => s; }",
        "ambiguous",
        TestName = "a policy filtering two types")]
    [TestCase(
        "[Queryable] [ReturnableWith(typeof(P))] public class A { public int Id { get; set; } } [Queryable] public class B { public int Id { get; set; } } public sealed class P : IReturnablePolicy<B> { public IQueryable<B> Filter(IQueryable<B> s, ScryPolicyContext c) => s; }",
        "P",
        TestName = "a policy attached outside the hierarchy it filters")]
    [TestCase(
        "[Queryable] [PreviousNames(\"\")] public class A { public int Id { get; set; } }",
        "contains a blank name",
        TestName = "a blank previous name")]
    [TestCase(
        "[Queryable] [PreviousNames(\"A\")] public class A { public int Id { get; set; } }",
        "already its current source name",
        TestName = "a previous name that is the current name")]
    [TestCase(
        "[Queryable] [PreviousNames(\"Old\")] public class A { public int Id { get; set; } } [Queryable] [PreviousNames(\"Old\")] public class B { public int Id { get; set; } }",
        "already a previous name of source",
        TestName = "a previous name claimed twice")]
    [TestCase(
        "[Queryable] public class A { public int Id { get; set; } [QueryIgnore] [PreviousNames(\"Old\")] public int Hidden { get; set; } }",
        "not exposed to clients",
        TestName = "a previous name on a hidden member")]
    [TestCase(
        "[QueryableComplex] [PreviousNames(\"Old\")] public class C { public int X { get; set; } } [Queryable] public class A { public int Id { get; set; } public C Part { get; set; } = new(); }",
        "has no effect",
        TestName = "a previous name on a complex type")]
    [TestCase(
        "[PreviousNames(\"Old\")] public class Plain { public int Id { get; set; } }",
        "has no wire name",
        TestName = "a previous name on a type that is not a source")]
    public void RefusesToStart(string model, string expected)
    {
        var exception = Refusal(model);

        Assert.That(exception.Message, Does.Contain(expected));
    }

    static Exception Refusal(string model)
    {
        var source =
            $$"""
              using System.Linq;
              using Microsoft.EntityFrameworkCore;
              using Scry;

              public sealed class ShapesContext : DbContext
              {
              }

              {{model}}
              """;
        var compilation = CSharpCompilation.Create(
            $"Shapes{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(source)],
            References(),
            new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        var errors = emitted.Diagnostics.Where(_ => _.Severity == DiagnosticSeverity.Error).ToList();
        Assert.That(errors, Is.Empty, string.Join("\n", errors));

        var assembly = Assembly.Load(stream.ToArray());
        var options = new ScryOptions(assembly.GetType("ShapesContext")!);
        return Assert.Throws<Exception>(() => Schema.Build(options))!;
    }

    static List<MetadataReference> References()
    {
        var trusted = (string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trusted
            .Split(Path.PathSeparator)
            .Where(_ => _.Length > 0)
            .Select(MetadataReference (_) => MetadataReference.CreateFromFile(_))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(QueryableAttribute).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(ScryProcessor).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(DbContext).Assembly.Location));
        return references;
    }
}
