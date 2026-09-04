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
                // Deprecated server-side: still queryable, so a snippet using it compiles and warns.
                new("Status", "Status", NeedsNullDefault: false, IsNavigation: false)
                {
                    Obsolete = "Use Active."
                },
                new("Manager", "EmployeeQueryModel?", NeedsNullDefault: false, IsNavigation: true),
                // A complex type is exposed exactly like a navigation member on the client model.
                new("Address", "AddressQueryModel?", NeedsNullDefault: false, IsNavigation: true)
            ]),
            new("AddressQueryModel",
            [
                new("City", "string", NeedsNullDefault: true, IsNavigation: false),
                new("Country", "string", NeedsNullDefault: true, IsNavigation: false)
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
        const string code = "Query.Employee.Where(_ => _.";
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
            "Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name, _.Status })");

        Assert.That(request.Root, Is.EqualTo("Employee"));
        Assert.That(request.Pipeline.Any(_ => _ is WhereOp), Is.True, "where op");
        Assert.That(request.Pipeline.Any(_ => _ is SelectOp), Is.True, "select op");
    }

    [Test]
    public async Task CompletesComplexTypeMembersAfterTraversal()
    {
        // Traversing into a complex member offers its scalar leaves, just like a navigation.
        const string code = "Query.Employee.Where(_ => _.Address.";
        var labels = (await workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        Assert.That(labels, Does.Contain("City"));
        Assert.That(labels, Does.Contain("Country"));
    }

    [Test]
    public void TranslatesComplexTypeTraversalToWire()
    {
        var request = executor.Translate(
            "Query.Employee.Where(_ => _.Address.City == \"London\").Select(_ => new { _.Name, _.Address.Country })");

        Assert.That(request.Root, Is.EqualTo("Employee"));
        Assert.That(request.Pipeline.Any(_ => _ is WhereOp), Is.True, "where op");
        Assert.That(request.Pipeline.Any(_ => _ is SelectOp), Is.True, "select op");
    }

    // The user's question and the full terminal-support surface: each terminal (Scry async or plain
    // LINQ) folds into the right wire QueryOp; a bare/list terminal adds none. Trailing ';' tolerated.
    [TestCase("Query.Employee.ToList()", null)]
    [TestCase("Query.Employee.ToList();", null)]
    [TestCase("Query.Employee.ToListAsync()", null)]
    [TestCase("Query.Employee.ToArray()", null)]
    [TestCase("Query.Employee.ToArrayAsync()", null)]
    [TestCase("Query.Employee.ToHashSet()", null)]
    [TestCase("Query.Employee.ToHashSetAsync()", null)]
    [TestCase("Query.Employee.ToDictionary(_ => _.Name)", null)]
    [TestCase("Query.Employee.ToDictionaryAsync(_ => _.Name)", null)]
    [TestCase("Query.Employee.Where(_ => _.Active).ToDictionaryAsync(_ => _.Name, _ => _.Status)", null)]
    [TestCase("Query.Employee.ToLookup(_ => _.Status)", null)]
    [TestCase("Query.Employee.ToLookupAsync(_ => _.Status)", null)]
    [TestCase("Query.Employee.CountAsync()", typeof(CountOp))]
    [TestCase("Query.Employee.Count()", typeof(CountOp))]
    [TestCase("Query.Employee.Where(_ => _.Active).CountAsync()", typeof(CountOp))]
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

    // A deprecated member stays fully queryable — the server validates and executes it either way — so
    // the snippet must compile and warn rather than fail. The message is the model's own.
    [Test]
    public async Task WarnsWithoutErroringOnAnObsoleteMember()
    {
        var diagnostics = await workspace.DiagnoseAsync("Query.Employee.Select(_ => new { _.Status })");

        Assert.That(diagnostics.Any(_ => !_.IsError && _.Message.Contains("Use Active.")), Is.True);
        Assert.That(diagnostics.Any(_ => _.IsError), Is.False);
    }

    [Test]
    public void TranslatesAnObsoleteMemberLikeAnyOther()
    {
        var request = executor.Translate("Query.Employee.Select(_ => new { _.Status })");

        Assert.That(request.Root, Is.EqualTo("Employee"));
        Assert.That(request.Pipeline.Any(_ => _ is SelectOp), Is.True, "select op");
    }

    [Test]
    public void SynthesizesExecutableModel()
    {
        var source = ModelSynthesizer.Synthesize(introspection, executable: true);

        Assert.That(source, Does.Contain("public enum Status"));
        Assert.That(source, Does.Contain("public class EmployeeQueryModel"));
        Assert.That(source, Does.Contain("public string Name { get; init; } = null!;"));
        Assert.That(source, Does.Contain("IQueryable<EmployeeQueryModel> Employee"));
        // Mirrors the generator, so a snippet warns exactly where compiled client code would. The
        // pragma keeps the synthesized model's own uses of a deprecated type quiet.
        Assert.That(source, Does.Contain("#pragma warning disable CS0612, CS0618"));
        Assert.That(source, Does.Contain("[global::System.ObsoleteAttribute(\"Use Active.\")]"));
        // The scalar member list mirrors the generator's entry point, so a snippet without a Select
        // produces the same wire request a generated client would.
        Assert.That(source, Does.Contain("client.Source<EmployeeQueryModel>(\"Employee\", [\"Name\", \"Active\", \"Status\"])"));
    }

    // Variables declared ahead of the query. A variable is captured state like any other, so what the
    // query reads from it folds into the constant it stood for — the request is the one the query
    // would have produced with the value written inline, and carries no trace of the name.
    [Test]
    public void FoldsAVariableIntoTheConstantItStandsFor()
    {
        var request = executor.Translate(
            """
            var name = "Ada";
            Query.Employee.Where(_ => _.Name == name)
            """);

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        Assert.That(((MemberNode) predicate.Left).Path, Is.EqualTo(["Name"]));
        Assert.That(predicate.Right, Is.EqualTo(new ConstNode("Ada", ClrTypeTag.String)));
    }

    // A variable holding a set is the same story one level out: it is evaluated here and its elements
    // become the constants of an In, which is the SQL IN a client-side set has always translated to.
    [Test]
    public void FoldsASetVariableIntoTheValuesItHolds()
    {
        var request = executor.Translate(
            """
            var wanted = new[] { "Ada", "Grace" };
            Query.Employee.Where(_ => wanted.Contains(_.Name))
            """);

        var predicate = (CallNode) ((WhereOp) request.Pipeline[0]).Predicate;
        Assert.That(predicate.Function, Is.EqualTo(KnownFunction.In));
        Assert.That(
            predicate.Arguments,
            Is.EqualTo(new Node[] {new ConstNode("Ada", ClrTypeTag.String), new ConstNode("Grace", ClrTypeTag.String)}));
    }

    [Test]
    public void CarriesEveryVariableAQueryReads()
    {
        var request = executor.Translate(
            """
            var name = "Ada";
            var wanted = Status.Contractor;
            Query.Employee.Where(_ => _.Name == name && _.Status == wanted).CountAsync()
            """);

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        Assert.That(((BinaryNode) predicate.Left).Right, Is.EqualTo(new ConstNode("Ada", ClrTypeTag.String)));
        Assert.That(((BinaryNode) predicate.Right).Right, Is.EqualTo(new ConstNode("Contractor", ClrTypeTag.Enum)));
        Assert.That(request.Pipeline[^1], Is.TypeOf<CountOp>());
    }

    // Where the query ends is decided by parsing the snippet, not by looking for a ';'. A semicolon
    // inside a string literal separates nothing, and a scan would split the snippet in the middle of
    // this one.
    [Test]
    public void SplitsTheSnippetWhereTheParserDoes()
    {
        var request = executor.Translate(
            """
            var separator = ";";
            Query.Employee.Where(_ => _.Name == separator)
            """);

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        Assert.That(predicate.Right, Is.EqualTo(new ConstNode(";", ClrTypeTag.String)));
    }

    // An editor with nothing in it has nothing to be told about. The wrapper an empty snippet splices
    // into does not compile — a method returning nothing — but that complaint anchors on the inserted
    // return, and a snippet nobody has written yet must not be squiggled for it.
    [Test]
    public async Task SaysNothingAboutAnEmptySnippet()
    {
        Assert.That(await workspace.DiagnoseAsync(""), Is.Empty);
        Assert.That(await workspace.DiagnoseAsync("   \n  "), Is.Empty);
    }

    [Test]
    public async Task VariablesAheadOfAQueryDiagnoseClean()
    {
        var diagnostics = await workspace.DiagnoseAsync(
            """
            var name = "Ada";
            Query.Employee.Where(_ => _.Name == name).Select(_ => new { _.Name })
            """);

        Assert.That(diagnostics, Is.Empty);
    }

    // The snippet is not one run of text in the document Roslyn sees — a `return` is spliced in at the
    // split — so an offset past it has to be walked back further than one before it. Both directions
    // are pinned by asserting against the position in the snippet as the editor knows it.
    [Test]
    public async Task AnchorsADiagnosticInTheQueryPastTheVariables()
    {
        const string code =
            """
            var name = "Ada";
            Query.Employee.Where(_ => _.Nope == name)
            """;

        var diagnostic = (await workspace.DiagnoseAsync(code)).Single(_ => _.Message.Contains("Nope"));

        Assert.That(diagnostic.Start, Is.EqualTo(code.IndexOf("Nope", StringComparison.Ordinal)));
        Assert.That(diagnostic.End, Is.EqualTo(diagnostic.Start + "Nope".Length));
    }

    [Test]
    public async Task AnchorsADiagnosticInsideAVariable()
    {
        const string code =
            """
            var name = Nope;
            Query.Employee.Where(_ => _.Name == name)
            """;

        var diagnostic = (await workspace.DiagnoseAsync(code)).First(_ => _.Message.Contains("Nope"));

        Assert.That(diagnostic.Start, Is.EqualTo(code.IndexOf("Nope", StringComparison.Ordinal)));
    }

    [Test]
    public async Task CompletesWithinAVariable()
    {
        const string code =
            """
            var name = "ada".ToUpper();
            Query.Employee.Where(_ => _.Name == name)
            """;
        var caret = code.IndexOf("ToUpper", StringComparison.Ordinal);

        var completions = await workspace.CompleteAsync(code, caret);

        Assert.That(completions.Select(_ => _.Label), Does.Contain("ToUpper"));
        Assert.That(completions.Select(_ => _.Label), Does.Contain("Length"));
        Assert.That(completions.All(_ => _.ReplaceStart == caret), Is.True, "replace span in snippet coordinates");
    }

    [Test]
    public async Task CompletesInTheQueryPastTheVariables()
    {
        const string code =
            """
            var name = "Ada";
            Query.Employee.Where(_ => _.
            """;

        var completions = await workspace.CompleteAsync(code, code.Length);

        Assert.That(completions.Select(_ => _.Label), Does.Contain("Active"));
        Assert.That(completions.All(_ => _.ReplaceStart == code.Length), Is.True, "replace span in snippet coordinates");
    }

    [Test]
    public async Task HoversInTheQueryPastTheVariables()
    {
        const string code =
            """
            var wanted = true;
            Query.Employee.Where(_ => _.Active == wanted)
            """;
        var member = code.IndexOf("Active", StringComparison.Ordinal);

        var hover = await workspace.GetHoverAsync(code, member + 1);

        Assert.That(hover, Is.Not.Null);
        Assert.That(hover!.Text, Does.Contain("Active"));
        Assert.That(hover.Start, Is.EqualTo(member));
        Assert.That(hover.End, Is.EqualTo(member + "Active".Length));
    }

    // Everything a declaration holds is evaluated here and folds into a constant, so a statement that
    // is not one would run in the browser without changing the request it produced — and a loop the
    // single-threaded runtime never returns from would take the page with it. Refused by both halves:
    // the editor squiggles it, and the executor is reachable without an editor.
    [Test]
    public async Task RefusesAStatementThatIsNotAVariable()
    {
        const string code =
            """
            Console.WriteLine("hi");
            Query.Employee
            """;

        var diagnostics = await workspace.DiagnoseAsync(code);

        Assert.That(diagnostics.Any(_ => _.IsError && _.Message.Contains("variable declaration")), Is.True);
        Assert.That(
            Assert.Throws<Exception>(() => executor.Translate(code))!.Message,
            Does.Contain("variable declaration"));
    }
}
