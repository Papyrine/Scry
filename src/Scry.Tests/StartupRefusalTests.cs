/// <summary>
/// The model shapes <c>Schema.Build</c> refuses, each given a test. A shape that refuses to start
/// cannot live in this assembly — the schema scans the whole model assembly it is built for, so one
/// bad type would fail every fixture — so each case compiles its own model (<see cref="CompiledModel"/>)
/// and builds the schema over that. Pinned by message, since the message is the fix a host is told.
/// </summary>
public class StartupRefusalTests
{
    [Test]
    [Arguments(
        "[Queryable] public class A { public int Id { get; set; } [Attachment] public string Doc { get; set; } = \"\"; }",
        "[Attachment]",
        DisplayName = "an attachment that is not a byte array")]
    [Arguments(
        "[Queryable] public class A { public int Id { get; set; } [QueryIgnore] [Attachment] public byte[]? Doc { get; set; } }",
        "not exposed to clients",
        DisplayName = "an attachment on a hidden member")]
    [Arguments(
        "[Queryable] public class A { public int Id { get; set; } [Attachment] [BinaryTransfer] public byte[]? Doc { get; set; } }",
        "carries both [Attachment] and [BinaryTransfer]",
        DisplayName = "an attachment that is also a binary transfer")]
    [Arguments(
        "[QueryableComplex] public class C { [Attachment] public byte[]? Doc { get; set; } } [Queryable] public class A { public int Id { get; set; } public C Part { get; set; } = new(); }",
        "[Attachment]",
        DisplayName = "an attachment on a complex type")]
    [Arguments(
        "[Queryable] [AttachmentWith(typeof(P))] public class A { public int Id { get; set; } [Attachment(ContentType = \"nope\")] public byte[]? Doc { get; set; } } public sealed class P : IAttachmentPolicy<A> { public bool Authorize(ScryAttachmentContext c) => true; }",
        "not a media type",
        DisplayName = "a declared content type that is not a media type")]
    [Arguments(
        "[Queryable] public class A { public int Id { get; set; } [Attachment] public byte[]? Doc { get; set; } }",
        "attachment",
        DisplayName = "an attachment with no policy to authorize it")]
    [Arguments(
        "[Queryable(Name = \"Same\")] public class A { public int Id { get; set; } } [Queryable(Name = \"Same\")] public class B { public int Id { get; set; } }",
        "Duplicate queryable source name 'Same'",
        DisplayName = "two sources with one name")]
    [Arguments(
        "[Queryable(Name = \"not valid\")] public class A { public int Id { get; set; } }",
        "not valid",
        DisplayName = "a source name that is not an identifier")]
    [Arguments(
        "[Queryable] [ReturnableWith(typeof(P))] public class A { public int Id { get; set; } } [Queryable] public class B { public int Id { get; set; } } public sealed class P : IReturnablePolicy<A>, IReturnablePolicy<B> { public IQueryable<A> Filter(IQueryable<A> s, ScryPolicyContext c) => s; public IQueryable<B> Filter(IQueryable<B> s, ScryPolicyContext c) => s; }",
        "ambiguous",
        DisplayName = "a policy filtering two types")]
    [Arguments(
        "[Queryable] [ReturnableWith(typeof(P))] public class A { public int Id { get; set; } } [Queryable] public class B { public int Id { get; set; } } public sealed class P : IReturnablePolicy<B> { public IQueryable<B> Filter(IQueryable<B> s, ScryPolicyContext c) => s; }",
        "P",
        DisplayName = "a policy attached outside the hierarchy it filters")]
    [Arguments(
        "[Queryable] [PreviousNames(\"\")] public class A { public int Id { get; set; } }",
        "contains a blank name",
        DisplayName = "a blank previous name")]
    [Arguments(
        "[Queryable] [PreviousNames(\"A\")] public class A { public int Id { get; set; } }",
        "already its current source name",
        DisplayName = "a previous name that is the current name")]
    [Arguments(
        "[Queryable] [PreviousNames(\"Old\")] public class A { public int Id { get; set; } } [Queryable] [PreviousNames(\"Old\")] public class B { public int Id { get; set; } }",
        "already a previous name of source",
        DisplayName = "a previous name claimed twice")]
    [Arguments(
        "[Queryable] public class A { public int Id { get; set; } [QueryIgnore] [PreviousNames(\"Old\")] public int Hidden { get; set; } }",
        "not exposed to clients",
        DisplayName = "a previous name on a hidden member")]
    [Arguments(
        "[QueryableComplex] [PreviousNames(\"Old\")] public class C { public int X { get; set; } } [Queryable] public class A { public int Id { get; set; } public C Part { get; set; } = new(); }",
        "has no effect",
        DisplayName = "a previous name on a complex type")]
    [Arguments(
        "[PreviousNames(\"Old\")] public class Plain { public int Id { get; set; } }",
        "has no wire name",
        DisplayName = "a previous name on a type that is not a source")]
    [Arguments(
        "public class Plain { public int Id { get; set; } }",
        "Nothing in assembly",
        DisplayName = "a model assembly with nothing opted in")]
    [Arguments(
        "[Queryable] public class E<T> { public int Id { get; set; } }",
        "'E' carries [Queryable] but is generic",
        DisplayName = "a generic type opted in")]
    [Arguments(
        "[Queryable(Name = \"E\")] public class E<T> { public int Id { get; set; } }",
        "but is generic",
        DisplayName = "a generic type opted in under a name")]
    [Arguments(
        "[QueryableComplex] public class C<T> { public int X { get; set; } } [Queryable] public class A { public int Id { get; set; } }",
        "'C' carries [QueryableComplex] but is generic",
        DisplayName = "a generic complex type")]
    [Arguments(
        "public abstract class E<T> { public T Day { get; set; } = default!; } [Queryable] public class A : E<System.DayOfWeek> { public int Id { get; set; } }",
        "'DayOfWeek', an enum declared in assembly",
        DisplayName = "a generic base filled in with a foreign enum")]
    [Arguments(
        "[Queryable] public class A : ForeignIgnoredBase { public int Id { get; set; } public override string Secret { get; set; } = \"\"; }",
        "Repeat [QueryIgnore] on the override",
        DisplayName = "an override losing a foreign [QueryIgnore]")]
    [Arguments(
        "[Queryable] public class A : ForeignSensitiveBase { public int Id { get; set; } public override string Salary { get; set; } = \"\"; }",
        "Repeat [Sensitive] on the override",
        DisplayName = "an override losing a foreign [Sensitive]")]
    [Arguments(
        "[Queryable] public class A : ForeignAttachmentBase { public int Id { get; set; } public override byte[]? Document { get; set; } }",
        "Repeat [Attachment] on the override",
        DisplayName = "an override losing a foreign [Attachment]")]
    [Arguments(
        "[Queryable] public class A : ForeignCollectionBase { public int Id { get; set; } public override List<string> Tags { get; set; } = []; }",
        "Repeat [QueryableCollection] on the override",
        DisplayName = "an override losing a foreign [QueryableCollection]")]
    [Arguments(
        "[Queryable] public class A : ForeignKeyBase { public int Id { get; set; } public override int Code { get; set; } }",
        "Repeat [Key] on the override",
        DisplayName = "an override losing a foreign [Key]")]
    [Arguments(
        "[Queryable] public class Monthly : ForeignKeylessBase { public int Month { get; set; } }",
        "Repeat [Keyless] on 'Monthly'",
        DisplayName = "a [Keyless] only a foreign base carries")]
    [Arguments(
        "public class Envelope<T> { public T Value { get; set; } = default!; } [Command(Result = typeof(Envelope<int>))] public class Touch { public int Id { get; set; } }",
        "'Touch' answers with 'Envelope', which is generic",
        DisplayName = "a command answering with a closed generic class")]
    [Arguments(
        "public class Envelope<T> { public int Count { get; set; } } [Command(Result = typeof(Envelope<>))] public class Touch { public int Id { get; set; } }",
        "'Touch' answers with 'Envelope', which is generic",
        DisplayName = "a command answering with an open generic class")]
    [Arguments(
        "[Command] public class Stamp : ForeignCommandIgnoredBase { public int Id { get; set; } public override string By { get; set; } = \"\"; }",
        "Repeat [CommandIgnore] on the override",
        DisplayName = "an override losing a foreign [CommandIgnore]")]
    public async Task RefusesToStart(string model, string expected)
    {
        var exception = Refusal(model);

        await Assert.That(exception.Message).Contains(expected);
    }

    // The layout a model package invites: the annotated types in the package's assembly, the context
    // declared by the host. Read from the context's assembly the model is empty, which is refused rather
    // than served; named through ModelAssembly it is read where it lives.
    [Test]
    public async Task ReadsTheModelAssemblyItIsGiven()
    {
        var model = CompiledModel.Compile(
            """
            [Queryable] public class A { public int Id { get; set; } }
            [Command] public sealed class Ping { }
            """);
        var host = CompiledModel.Compile("public sealed class HostContext : DbContext { }");
        var context = host.Assembly.GetType("HostContext")!;

        var refusal = Assert.ThrowsExactly<Exception>(() => Schema.Build(new(context)));

        var schema = Schema.Build(new(context)
        {
            ModelAssembly = model.Assembly
        });

        using (Assert.Multiple())
        {
            await Assert.That(refusal.Message).Contains("ScryOptions.ModelAssembly");
            await Assert.That(schema.TryGetSource("A", out _)).IsTrue();
            await Assert.That(schema.TryGetCommand("Ping", out _)).IsTrue();
        }
    }

    static Exception Refusal(string model)
    {
        var (assembly, _) = CompiledModel.Compile(
            $$"""
              public sealed class ShapesContext : DbContext
              {
              }

              {{model}}
              """);
        var options = new ScryOptions(assembly.GetType("ShapesContext")!);
        return Assert.ThrowsExactly<Exception>(() => Schema.Build(options));
    }
}
