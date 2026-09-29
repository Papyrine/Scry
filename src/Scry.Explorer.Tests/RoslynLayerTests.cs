// In-process tests of the browser-Roslyn layer. These run on the desktop host (not WASM), so they are
// fast and deterministic — they cover the completion/diagnostics/translation LOGIC. The Playwright
// suite (samples/Sample.Tests) remains the thin layer that proves it all actually works inside WASM.
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
    static RoslynWorkspace workspace =
        RoslynWorkspace.Create(ModelSynthesizer.Synthesize(introspection), scryReferences);

    static SnippetExecutor executor = SnippetExecutor.Create(introspection, scryReferences);

    // A request forks the solution with the snippet's text and derives its compilation from the one
    // the base has in hand — one tree replaced, the rest kept — but only when the base has one.
    // Requests do not put one there: each fork's compilation is the fork's own, so cold, every
    // completion, diagnostic pass, and hover built from nothing. Warming is what puts one there, and
    // it stays there through the requests that follow.
    [Test]
    public async Task HoldsTheBaseCompilationOnceWarmed()
    {
        using var fresh = RoslynWorkspace.Create(ModelSynthesizer.Synthesize(introspection), scryReferences);
        await Assert.That(fresh.IsWarm).IsFalse();

        await fresh.DiagnoseAsync("Query.Employee.Where(_ => _.Active)");
        await Assert.That(fresh.IsWarm).IsFalse().Because("a request warms nothing");

        await fresh.WarmAsync();
        await Assert.That(fresh.IsWarm).IsTrue();

        await fresh.DiagnoseAsync("Query.Employee.Where(_ => _.Name.Length > 1)");
        await fresh.CompleteAsync("Query.Employee.Where(_ => _.", 26);
        await Assert.That(fresh.IsWarm).IsTrue().Because("requests leave it in place");
    }

    [Test]
    public async Task CompletesModelMembersAfterLambdaDot()
    {
        const string code = "Query.Employee.Where(_ => _.";
        var labels = (await workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        await Assert.That(labels).Contains("Active");
        await Assert.That(labels).Contains("Name");
        await Assert.That(labels).Contains("Status");
        await Assert.That(labels).Contains("Manager");
    }

    [Test]
    public async Task CompletesTerminalsAfterQueryable()
    {
        const string code = "Query.Employee.";
        var labels = (await workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        await Assert.That(labels).Contains("Where");
        await Assert.That(labels).Contains("ToListAsync");
        await Assert.That(labels).Contains("FirstAsync");
        await Assert.That(labels).Contains("CountAsync");
    }

    [Test]
    public async Task DiagnosesUnknownMember()
    {
        var diagnostics = await workspace.DiagnoseAsync("Query.Employee.Where(_ => _.Nope)");

        await Assert.That(diagnostics.Any(_ => _.IsError && _.Message.Contains("Nope"))).IsTrue();
    }

    [Test]
    public async Task ValidQueryHasNoDiagnostics()
    {
        var diagnostics = await workspace.DiagnoseAsync(
            "Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name })");

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task TranslatesWhereSelectToWire()
    {
        var request = executor.Translate(
            "Query.Employee.Where(_ => _.Active).Select(_ => new { _.Name, _.Status })");

        await Assert.That(request.Root).IsEqualTo("Employee");
        await Assert.That(request.Pipeline.Any(_ => _ is WhereOp)).IsTrue().Because("where op");
        await Assert.That(request.Pipeline.Any(_ => _ is SelectOp)).IsTrue().Because("select op");
    }

    [Test]
    public async Task CompletesComplexTypeMembersAfterTraversal()
    {
        // Traversing into a complex member offers its scalar leaves, just like a navigation.
        const string code = "Query.Employee.Where(_ => _.Address.";
        var labels = (await workspace.CompleteAsync(code, code.Length)).Select(_ => _.Label).ToList();

        await Assert.That(labels).Contains("City");
        await Assert.That(labels).Contains("Country");
    }

    [Test]
    public async Task TranslatesComplexTypeTraversalToWire()
    {
        var request = executor.Translate(
            "Query.Employee.Where(_ => _.Address.City == \"London\").Select(_ => new { _.Name, _.Address.Country })");

        await Assert.That(request.Root).IsEqualTo("Employee");
        await Assert.That(request.Pipeline.Any(_ => _ is WhereOp)).IsTrue().Because("where op");
        await Assert.That(request.Pipeline.Any(_ => _ is SelectOp)).IsTrue().Because("select op");
    }

    // The user's question and the full terminal-support surface: each terminal (Scry async or plain
    // LINQ) folds into the right wire QueryOp; a bare/list terminal adds none. Trailing ';' tolerated.
    [Test]
    [Arguments("Query.Employee.ToList()", null)]
    [Arguments("Query.Employee.ToList();", null)]
    [Arguments("Query.Employee.ToListAsync()", null)]
    [Arguments("Query.Employee.ToArray()", null)]
    [Arguments("Query.Employee.ToArrayAsync()", null)]
    [Arguments("Query.Employee.ToHashSet()", null)]
    [Arguments("Query.Employee.ToHashSetAsync()", null)]
    [Arguments("Query.Employee.ToDictionary(_ => _.Name)", null)]
    [Arguments("Query.Employee.ToDictionaryAsync(_ => _.Name)", null)]
    [Arguments("Query.Employee.Where(_ => _.Active).ToDictionaryAsync(_ => _.Name, _ => _.Status)", null)]
    [Arguments("Query.Employee.ToLookup(_ => _.Status)", null)]
    [Arguments("Query.Employee.ToLookupAsync(_ => _.Status)", null)]
    [Arguments("Query.Employee.CountAsync()", typeof(CountOp))]
    [Arguments("Query.Employee.Count()", typeof(CountOp))]
    [Arguments("Query.Employee.Where(_ => _.Active).CountAsync()", typeof(CountOp))]
    [Arguments("Query.Employee.FirstAsync()", typeof(FirstOp))]
    [Arguments("Query.Employee.SingleAsync()", typeof(SingleOp))]
    [Arguments("Query.Employee.AnyAsync()", typeof(AnyOp))]
    public async Task TranslatesTerminalToWireOp(string query, Type? terminalOp)
    {
        var request = executor.Translate(query);

        await Assert.That(request.Root).IsEqualTo("Employee");
        if (terminalOp is null)
        {
            await Assert.That(request.Pipeline.Any(_ => _ is CountOp or AnyOp or FirstOp or SingleOp)).IsFalse().Because("a list/enumerate terminal should add no terminal op");
        }
        else
        {
            await Assert.That(request.Pipeline[^1].GetType()).IsEqualTo(terminalOp);
        }
    }

    // A deprecated member stays fully queryable — the server validates and executes it either way — so
    // the snippet must compile and warn rather than fail. The message is the model's own.
    [Test]
    public async Task WarnsWithoutErroringOnAnObsoleteMember()
    {
        var diagnostics = await workspace.DiagnoseAsync("Query.Employee.Select(_ => new { _.Status })");

        await Assert.That(diagnostics.Any(_ => !_.IsError && _.Message.Contains("Use Active."))).IsTrue();
        await Assert.That(diagnostics.Any(_ => _.IsError)).IsFalse();
    }

    [Test]
    public async Task TranslatesAnObsoleteMemberLikeAnyOther()
    {
        var request = executor.Translate("Query.Employee.Select(_ => new { _.Status })");

        await Assert.That(request.Root).IsEqualTo("Employee");
        await Assert.That(request.Pipeline.Any(_ => _ is SelectOp)).IsTrue().Because("select op");
    }

    [Test]
    public async Task SynthesizesExecutableModel()
    {
        var source = ModelSynthesizer.Synthesize(introspection, executable: true);

        await Assert.That(source).Contains("public enum Status");
        await Assert.That(source).Contains("public class EmployeeQueryModel");
        await Assert.That(source).Contains("public string Name { get; init; } = null!;");
        await Assert.That(source).Contains("IQueryable<EmployeeQueryModel> Employee");
        // Mirrors the generator, so a snippet warns exactly where compiled client code would. The
        // pragma keeps the synthesized model's own uses of a deprecated type quiet.
        await Assert.That(source).Contains("#pragma warning disable CS0612, CS0618");
        await Assert.That(source).Contains("[global::System.ObsoleteAttribute(\"Use Active.\")]");
        // The scalar member list mirrors the generator's entry point, so a snippet without a Select
        // produces the same wire request a generated client would.
        await Assert.That(source).Contains("client.Source<EmployeeQueryModel>(\"Employee\", [\"Name\", \"Active\", \"Status\"])");
    }

    // A member or enum value named with a reserved keyword is spelled with the verbatim prefix, as
    // the generator spells it; the wire name introspection carries is the bare one.
    [Test]
    public async Task SynthesizesKeywordNamesEscaped()
    {
        var keyworded = new ScryIntrospection(
            ScryIntrospection.CurrentVersion,
            MaxPageSize: 200,
            Sources: [new("Event", "EfCore", "EventQueryModel")],
            Types:
            [
                new("EventQueryModel",
                [
                    new("event", "string", NeedsNullDefault: true, IsNavigation: false),
                    new("class", "Kind", NeedsNullDefault: false, IsNavigation: false)
                ])
            ],
            Enums: [new("Kind", ["default", "override"])]);

        var source = ModelSynthesizer.Synthesize(keyworded);

        using (Assert.Multiple())
        {
            await Assert.That(source).Contains("public string @event { get; init; }");
            await Assert.That(source).Contains("public Kind @class { get; init; }");
            await Assert.That(source).Contains("    @default,");
            await Assert.That(source).Contains("    @override,");
        }
    }

    // Variables declared ahead of the query. A variable is captured state like any other, so what the
    // query reads from it folds into the constant it stood for — the request is the one the query
    // would have produced with the value written inline, and carries no trace of the name.
    [Test]
    public async Task FoldsAVariableIntoTheConstantItStandsFor()
    {
        var request = executor.Translate(
            """
            var name = "Ada";
            Query.Employee.Where(_ => _.Name == name)
            """);

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        await Assert.That(((MemberNode) predicate.Left).Path).IsEquivalentTo(["Name"], CollectionOrdering.Matching);
        await Assert.That(predicate.Right).IsEqualTo(new ConstNode("Ada", ClrTypeTag.String));
    }

    // A variable holding a set is the same story one level out: it is evaluated here and its elements
    // become the constants of an In, which is the SQL IN a client-side set has always translated to.
    [Test]
    public async Task FoldsASetVariableIntoTheValuesItHolds()
    {
        var request = executor.Translate(
            """
            var wanted = new[] { "Ada", "Grace" };
            Query.Employee.Where(_ => wanted.Contains(_.Name))
            """);

        var predicate = (CallNode) ((WhereOp) request.Pipeline[0]).Predicate;
        await Assert.That(predicate.Function).IsEqualTo(KnownFunction.In);
        await Assert.That(predicate.Arguments).IsEquivalentTo(new Node[] {new ConstNode("Ada", ClrTypeTag.String), new ConstNode("Grace", ClrTypeTag.String)}, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CarriesEveryVariableAQueryReads()
    {
        var request = executor.Translate(
            """
            var name = "Ada";
            var wanted = Status.Contractor;
            Query.Employee.Where(_ => _.Name == name && _.Status == wanted).CountAsync()
            """);

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        await Assert.That(((BinaryNode) predicate.Left).Right).IsEqualTo(new ConstNode("Ada", ClrTypeTag.String));
        await Assert.That(((BinaryNode) predicate.Right).Right).IsEqualTo(new ConstNode("Contractor", ClrTypeTag.Enum));
        await Assert.That(request.Pipeline[^1]).IsTypeOf<CountOp>();
    }

    // Where the query ends is decided by parsing the snippet, not by looking for a ';'. A semicolon
    // inside a string literal separates nothing, and a scan would split the snippet in the middle of
    // this one.
    [Test]
    public async Task SplitsTheSnippetWhereTheParserDoes()
    {
        var request = executor.Translate(
            """
            var separator = ";";
            Query.Employee.Where(_ => _.Name == separator)
            """);

        var predicate = (BinaryNode) ((WhereOp) request.Pipeline[0]).Predicate;
        await Assert.That(predicate.Right).IsEqualTo(new ConstNode(";", ClrTypeTag.String));
    }

    // An editor with nothing in it has nothing to be told about. The wrapper an empty snippet splices
    // into does not compile — a method returning nothing — but that complaint anchors on the inserted
    // return, and a snippet nobody has written yet must not be squiggled for it.
    [Test]
    public async Task SaysNothingAboutAnEmptySnippet()
    {
        await Assert.That(await workspace.DiagnoseAsync("")).IsEmpty();
        await Assert.That(await workspace.DiagnoseAsync("   \n  ")).IsEmpty();
    }

    [Test]
    public async Task VariablesAheadOfAQueryDiagnoseClean()
    {
        var diagnostics = await workspace.DiagnoseAsync(
            """
            var name = "Ada";
            Query.Employee.Where(_ => _.Name == name).Select(_ => new { _.Name })
            """);

        await Assert.That(diagnostics).IsEmpty();
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

        await Assert.That(diagnostic.Start).IsEqualTo(code.IndexOf("Nope", StringComparison.Ordinal));
        await Assert.That(diagnostic.End).IsEqualTo(diagnostic.Start + "Nope".Length);
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

        await Assert.That(diagnostic.Start).IsEqualTo(code.IndexOf("Nope", StringComparison.Ordinal));
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

        await Assert.That(completions.Select(_ => _.Label)).Contains("ToUpper");
        await Assert.That(completions.Select(_ => _.Label)).Contains("Length");
        await Assert.That(completions.All(_ => _.ReplaceStart == caret)).IsTrue().Because("replace span in snippet coordinates");
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

        await Assert.That(completions.Select(_ => _.Label)).Contains("Active");
        await Assert.That(completions.All(_ => _.ReplaceStart == code.Length)).IsTrue().Because("replace span in snippet coordinates");
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

        await Assert.That(hover).IsNotNull();
        await Assert.That(hover!.Text).Contains("Active");
        await Assert.That(hover.Start).IsEqualTo(member);
        await Assert.That(hover.End).IsEqualTo(member + "Active".Length);
    }

    // The models are compiled and loaded once for the schema; a run compiles only its own snippet
    // against them. Before, every run re-emitted the whole model beside its snippet and loaded the
    // result into a context nothing can unload, so a hundred runs held a hundred copies of it.
    [Test]
    public async Task LoadsTheModelOncePerSchema()
    {
        var local = SnippetExecutor.Create(introspection, scryReferences);
        for (var run = 0; run < 3; run++)
        {
            local.Translate($"Query.Employee.Where(_ => _.Name.Length > {run})");
        }

        var names = local.LoadedAssemblies.Select(_ => _.GetName().Name!).ToList();
        await Assert.That(names.Count(_ => _ == "ScryModel")).IsEqualTo(1);
        await Assert.That(names.Count(_ => _.StartsWith("ScrySnippet"))).IsEqualTo(3);
    }

    // The snippet runs through reflection, which wraps whatever it throws in an exception whose own
    // message says only that something was thrown. What the translator refuses — here a string method
    // that is client-side code — has to reach the banner as the refusal itself.
    [Test]
    public async Task ReportsATranslatorRefusalAsItself()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => executor.Translate("Query.Employee.Where(_ => _.Name.GetHashCode() == 3)"));

        await Assert.That(exception.Message).Contains("GetHashCode").And.Contains("client-side code");
    }

    // A variable's initializer is the snippet's own code, and what it throws is reported as what it
    // threw.
    [Test]
    public async Task ReportsAFailedInitializerAsItself()
    {
        const string code =
            """
            var count = int.Parse("nope");
            Query.Employee.Where(_ => _.Name.Length == count)
            """;

        var exception = Assert.ThrowsExactly<FormatException>(() => executor.Translate(code));

        await Assert.That(exception.Message).Contains("nope");
    }

    // Everything a declaration holds is evaluated here and folds into a constant, so a statement that
    // is not one would run in the browser without changing the request it produced. Refused by both
    // halves: the editor squiggles it, and the executor is reachable without an editor.
    [Test]
    public async Task RefusesAStatementThatIsNotAVariable()
    {
        const string code =
            """
            Console.WriteLine("hi");
            Query.Employee
            """;

        var diagnostics = await workspace.DiagnoseAsync(code);

        await Assert.That(diagnostics.Any(_ => _.IsError && _.Message.Contains("variable declaration"))).IsTrue();
        await Assert.That(Assert.ThrowsExactly<Exception>(() => executor.Translate(code)).Message).Contains("variable declaration");
    }

    // The rule above is about the snippet's shape, not a boundary around what runs: a declaration's
    // initializer is ordinary code, evaluated in the browser exactly as a compiled client would
    // evaluate it, and what it produced is what the query folds in.
    [Test]
    public async Task EvaluatesADeclarationsInitializerAsOrdinaryCode()
    {
        const string code =
            """
            var name = string.Concat("Aa", "ron");
            Query.Employee.Where(_ => _.Name == name)
            """;

        var request = executor.Translate(code);

        await Assert.That(ScryJson.Serialize(request)).Contains("Aaron").And.DoesNotContain("Concat");
    }
}
