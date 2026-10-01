// Members typed as BCL enums, as the explorer meets them: spelled in introspection as the BCL type,
// with no enum described for it, exactly as the server and the generator spell them. The synthesized
// model has to compile against the type every client already has, and a snippet over it has to send
// what generated client code would.
public class BclEnumExplorerTests
{
    [ScryModel("Sitting", "Id", "DayOfWeek", "Recess", "Clock")]
    public class SittingQueryModel
    {
        public int Id { get; init; }
        public DayOfWeek DayOfWeek { get; init; }
        public DayOfWeek? Recess { get; init; }
        public DateTimeKind Clock { get; init; }
        public IReadOnlyList<DayOfWeek> Alternates { get; init; } = null!;
    }

    // The displays the server publishes for Sitting (Scry.Tests' LockstepTests pins them against the
    // generator's).
    static ScryIntrospection introspection = new(
        ScryIntrospection.CurrentVersion,
        MaxPageSize: 200,
        Sources: [new("Sitting", "EfCore", "SittingQueryModel")],
        Types:
        [
            new("SittingQueryModel",
            [
                new("Id", "int", NeedsNullDefault: false, IsNavigation: false),
                new("DayOfWeek", "global::System.DayOfWeek", NeedsNullDefault: false, IsNavigation: false),
                new("Recess", "global::System.DayOfWeek?", NeedsNullDefault: false, IsNavigation: false),
                new("Clock", "global::System.DateTimeKind", NeedsNullDefault: false, IsNavigation: false),
                new("Alternates", "global::System.Collections.Generic.IReadOnlyList<global::System.DayOfWeek>", NeedsNullDefault: true, IsNavigation: false, IsCollection: true)
            ])
        ],
        Enums: [])
    {
        SchemaStamp = "bcl-enums"
    };

    static IReadOnlyList<MetadataReference> scryReferences =
    [
        MetadataReference.CreateFromFile(typeof(ScryClient).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(QueryRequest).Assembly.Location)
    ];

    static RoslynWorkspace workspace =
        RoslynWorkspace.Create(ModelSynthesizer.Synthesize(introspection), scryReferences);

    static SnippetExecutor executor = SnippetExecutor.Create(introspection, scryReferences);

    static ScryClient client = new((_, _) => Task.FromResult<QueryResponse>(null!))
    {
        SchemaStamp = "bcl-enums"
    };

    static IQueryable<SittingQueryModel> Sitting =>
        client.Source<SittingQueryModel>("Sitting", typeof(SittingQueryModel).GetCustomAttribute<ScryModelAttribute>()!.Members);

    const string snippet = "Query.Sitting.Where(_ => _.DayOfWeek == DayOfWeek.Thursday && _.Recess != null && _.Clock == DateTimeKind.Local && _.Alternates.Contains(DayOfWeek.Friday)).OrderByDescending(_ => _.DayOfWeek).Select(_ => new { _.Id, _.Recess, _.Clock })";

    [Test]
    public async Task SpellsTheBclTypeAndDeclaresNoEnumForIt()
    {
        var source = ModelSynthesizer.Synthesize(introspection, executable: true);

        using (Assert.Multiple())
        {
            await Assert.That(source).Contains("public global::System.DayOfWeek DayOfWeek { get; init; }");
            await Assert.That(source).Contains("public global::System.DayOfWeek? Recess { get; init; }");
            await Assert.That(source).Contains("public global::System.DateTimeKind Clock { get; init; }");
            await Assert.That(source).DoesNotContain("public enum");
        }
    }

    [Test]
    public async Task ASnippetOverThemCompilesClean()
    {
        var diagnostics = await workspace.DiagnoseAsync(snippet);

        await Assert.That(diagnostics.Select(_ => _.Message)).IsEmpty();
    }

    // The snippet sends exactly the request generated client code would: the same LINQ over a model
    // typed the same way, captured by the real client.
    [Test]
    public async Task ASnippetTranslatesAsGeneratedCodeWould()
    {
        var translated = executor.Translate(snippet);
        var generated = Sitting
            .Where(_ => _.DayOfWeek == DayOfWeek.Thursday && _.Recess != null && _.Clock == DateTimeKind.Local && _.Alternates.Contains(DayOfWeek.Friday))
            .OrderByDescending(_ => _.DayOfWeek)
            .Select(_ => new {_.Id, _.Recess, _.Clock})
            .ToScryRequest();

        await Assert.That(ScryJson.Serialize(translated)).IsEqualTo(ScryJson.Serialize(generated));
    }

    // A captured request renders back to a snippet that sends it again, as the history and share links
    // need: a DayOfWeek constant arrives as its number and is spelled back as the enum value.
    [Test]
    public async Task ARequestRendersBackToASnippetThatSendsIt()
    {
        var request = Sitting
            .Where(_ => _.DayOfWeek == DayOfWeek.Thursday && _.Clock == DateTimeKind.Local)
            .Select(_ => new {_.Id, _.Recess})
            .ToScryRequest();

        await Assert.That(ScryQueryRenderer.TryRender(request, out var code, out var refusal)).IsTrue().Because($"render refused: {refusal}");
        var translated = executor.Translate(code!);
        await Assert.That(ScryJson.Serialize(translated)).IsEqualTo(ScryJson.Serialize(request)).Because(code!);
    }
}
