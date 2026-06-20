using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    static readonly ScryIntrospection Introspection = new(
        ScryIntrospection.CurrentVersion,
        MaxPageSize: 200,
        Sources: [new ScrySourceInfo("Employee", "EfCore", "EmployeeQueryModel")],
        Types:
        [
            new ScryTypeInfo("EmployeeQueryModel",
            [
                new ScryMemberInfo("Name", "string", NeedsNullDefault: true, IsNavigation: false),
                new ScryMemberInfo("Active", "bool", NeedsNullDefault: false, IsNavigation: false),
                new ScryMemberInfo("Status", "Status", NeedsNullDefault: false, IsNavigation: false),
                new ScryMemberInfo("Manager", "EmployeeQueryModel?", NeedsNullDefault: false, IsNavigation: true)
            ])
        ],
        Enums: [new ScryEnumInfo("Status", ["FullTime", "PartTime", "Contractor"])]);

    // The real Scry.Client/Scry.Wire assemblies on disk become the snippet's metadata references —
    // exactly what the browser fetches from _framework, minus the HTTP.
    static readonly IReadOnlyList<MetadataReference> ScryReferences =
    [
        MetadataReference.CreateFromFile(typeof(ScryClient).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(QueryRequest).Assembly.Location)
    ];

    // Shared across tests: building the MEF host / executor is the expensive part, and both are used
    // through functional (non-mutating) APIs, so they are safe to reuse.
    static readonly RoslynWorkspace Workspace =
        RoslynWorkspace.Create(ModelSynthesizer.Synthesize(Introspection), ScryReferences);

    static readonly SnippetExecutor Executor = SnippetExecutor.Create(Introspection, ScryReferences);

    [Test]
    public async Task CompletesModelMembersAfterLambdaDot()
    {
        const string code = "Query.Employee.Where(e => e.";
        var labels = (await Workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        Assert.That(labels, Does.Contain("Active"));
        Assert.That(labels, Does.Contain("Name"));
        Assert.That(labels, Does.Contain("Status"));
        Assert.That(labels, Does.Contain("Manager"));
    }

    [Test]
    public async Task CompletesTerminalsAfterQueryable()
    {
        const string code = "Query.Employee.";
        var labels = (await Workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        Assert.That(labels, Does.Contain("Where"));
        Assert.That(labels, Does.Contain("ToScryListAsync"));
        Assert.That(labels, Does.Contain("FirstScryAsync"));
        Assert.That(labels, Does.Contain("CountScryAsync"));
    }

    [Test]
    public async Task DiagnosesUnknownMember()
    {
        var diagnostics = await Workspace.DiagnoseAsync("Query.Employee.Where(e => e.Nope)");

        Assert.That(diagnostics.Any(_ => _.IsError && _.Message.Contains("Nope")), Is.True);
    }

    [Test]
    public async Task ValidQueryHasNoDiagnostics()
    {
        var diagnostics = await Workspace.DiagnoseAsync(
            "Query.Employee.Where(e => e.Active).Select(e => new { e.Name })");

        Assert.That(diagnostics, Is.Empty);
    }

    [Test]
    public void TranslatesWhereSelectToWire()
    {
        var request = Executor.Translate(
            "Query.Employee.Where(e => e.Active).Select(e => new { e.Name, e.Status })");

        Assert.That(request.Root, Is.EqualTo("Employee"));
        Assert.That(request.Pipeline.Any(_ => _ is WhereOp), Is.True, "where op");
        Assert.That(request.Pipeline.Any(_ => _ is SelectOp), Is.True, "select op");
    }

    // The user's question and the full terminal-support surface: each terminal (Scry async or plain
    // LINQ) folds into the right wire QueryOp; a bare/list terminal adds none. Trailing ';' tolerated.
    [TestCase("Query.Employee.ToList()", null)]
    [TestCase("Query.Employee.ToList();", null)]
    [TestCase("Query.Employee.ToScryListAsync()", null)]
    [TestCase("Query.Employee.CountScryAsync()", typeof(CountOp))]
    [TestCase("Query.Employee.Count()", typeof(CountOp))]
    [TestCase("Query.Employee.Where(e => e.Active).CountScryAsync()", typeof(CountOp))]
    [TestCase("Query.Employee.FirstScryAsync()", typeof(FirstOp))]
    [TestCase("Query.Employee.SingleScryAsync()", typeof(SingleOp))]
    [TestCase("Query.Employee.AnyScryAsync()", typeof(AnyOp))]
    public void TranslatesTerminalToWireOp(string query, Type? terminalOp)
    {
        var request = Executor.Translate(query);

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
        var source = ModelSynthesizer.Synthesize(Introspection, executable: true);

        Assert.That(source, Does.Contain("public enum Status"));
        Assert.That(source, Does.Contain("public sealed class EmployeeQueryModel"));
        Assert.That(source, Does.Contain("public string Name { get; init; } = null!;"));
        Assert.That(source, Does.Contain("IQueryable<EmployeeQueryModel> Employee"));
        Assert.That(source, Does.Contain("client.Source<EmployeeQueryModel>(\"Employee\")"));
    }
}
