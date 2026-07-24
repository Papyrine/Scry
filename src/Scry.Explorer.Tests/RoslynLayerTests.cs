using Microsoft.CodeAnalysis;
using NUnit.Framework;
using Scry.Client;
using Scry.Explorer.Core;
using Scry.Wire;

namespace Scry.Explorer.Tests;

// In-process tests of the browser-Roslyn layer. These run on the desktop host (not WASM), so they are
// fast and deterministic — they cover the completion/diagnostics/translation LOGIC. The Playwright
// suite (samples/Sample.Tests) remains the thin layer that proves it all actually works inside WASM.
[TestFixture]
public class RoslynLayerTests
{
    // A small allow-listed surface mirroring the sample's Employee model (no server/EF needed).
    static ScryIntrospection introspection = new(
        ScryIntrospection.CurrentVersion,
        MaxPageSize: 200,
        Sources: [new("Employee", "EfCore", "EmployeeQueryModel")],
        Types:
        [
            new("EmployeeQueryModel",
            [
                new("Name", "string", NeedsNullDefault: true, IsNavigation: false),
                new("Active", "bool", NeedsNullDefault: false, IsNavigation: false),
                new("Status", "Status", NeedsNullDefault: false, IsNavigation: false),
                new("Manager", "EmployeeQueryModel?", NeedsNullDefault: false, IsNavigation: true)
            ])
        ],
        Enums: [new("Status", ["FullTime", "PartTime", "Contractor"])]);

    // The real Scry.Client/Scry.Wire assemblies on disk become the snippet's metadata references —
    // exactly what the browser fetches from _framework, minus the HTTP.
    static IReadOnlyList<MetadataReference> scryReferences =
    [
        MetadataReference.CreateFromFile(typeof(ScryClient).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(QueryRequest).Assembly.Location)
    ];

    // Shared across tests: building the MEF host / executor is the expensive part, and both are used
    // through functional (non-mutating) APIs, so they are safe to reuse.
    static readonly RoslynWorkspace workspace =
        RoslynWorkspace.Create(ModelSynthesizer.Synthesize(introspection), scryReferences);

    static readonly SnippetExecutor executor = SnippetExecutor.Create(introspection, scryReferences);

    [Test]
    public async Task CompletesModelMembersAfterLambdaDot()
    {
        const string code = "Query.Employee.Where(e => e.";
        var labels = (await workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        Assert.That(labels, Does.Contain("Active"));
        Assert.That(labels, Does.Contain("Name"));
        Assert.That(labels, Does.Contain("Status"));
        Assert.That(labels, Does.Contain("Manager"));
    }

    [Test]
    public async Task CompletesTerminalsAfterQueryable()
    {
        const string code = "Query.Employee.";
        var labels = (await workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        Assert.That(labels, Does.Contain("Where"));
        Assert.That(labels, Does.Contain("ToListAsync"));
        Assert.That(labels, Does.Contain("FirstAsync"));
        Assert.That(labels, Does.Contain("CountAsync"));
    }

    [Test]
    public async Task DiagnosesUnknownMember()
    {
        var diagnostics = await workspace.DiagnoseAsync("Query.Employee.Where(_ => _.Nope)");

        Assert.That(diagnostics.Any(_ => _.IsError && _.Message.Contains("Nope")), Is.True);
    }

    [Test]
    public async Task ValidQueryHasNoDiagnostics()
    {
        var diagnostics = await workspace.DiagnoseAsync(
            "Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name })");

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void TranslatesWhereSelectToWire()
    {
        var request = executor.Translate(
            "Query.Employee.Where(e => e.Active).Select(e => new { e.Name, e.Status })");

        Assert.That(request.Root, Is.EqualTo("Employee"));
        Assert.That(request.Pipeline.Any(_ => _ is WhereOp), Is.True, "where op");
        Assert.That(request.Pipeline.Any(_ => _ is SelectOp), Is.True, "select op");
    }

    // The user's question and the full terminal-support surface: each terminal (Scry async or plain
    // LINQ) folds into the right wire QueryOp; a bare/list terminal adds none. Trailing ';' tolerated.
    [TestCase("Query.Employee.ToList()", null)]
    [TestCase("Query.Employee.ToList();", null)]
    [TestCase("Query.Employee.ToListAsync()", null)]
    [TestCase("Query.Employee.CountAsync()", typeof(CountOp))]
    [TestCase("Query.Employee.Count()", typeof(CountOp))]
    [TestCase("Query.Employee.Where(e => e.Active).CountAsync()", typeof(CountOp))]
    [TestCase("Query.Employee.FirstAsync()", typeof(FirstOp))]
    [TestCase("Query.Employee.SingleAsync()", typeof(SingleOp))]
    [TestCase("Query.Employee.AnyAsync()", typeof(AnyOp))]
    public void TranslatesTerminalToWireOp(string query, Type? terminalOp)
    {
        var request = executor.Translate(query);

        Assert.That(request.Root, Is.EqualTo("Employee"));
        if (terminalOp is null)
        {
            Assert.That(
                request.Pipeline.Any(_ => _ is CountOp or AnyOp or FirstOp or SingleOp),
                Is.False,
                "a list/enumerate terminal should add no terminal op");
        }
        else
        {
            Assert.That(request.Pipeline[^1], Is.TypeOf(terminalOp));
        }
    }

    [Test]
    public void SynthesizesExecutableModel()
    {
        var source = ModelSynthesizer.Synthesize(introspection, executable: true);

        Assert.That(source, Does.Contain("public enum Status"));
        Assert.That(source, Does.Contain("public sealed class EmployeeQueryModel"));
        Assert.That(source, Does.Contain("public string Name { get; init; } = null!;"));
        Assert.That(source, Does.Contain("IQueryable<EmployeeQueryModel> Employee"));
        Assert.That(source, Does.Contain("client.Source<EmployeeQueryModel>(\"Employee\")"));
    }
}
