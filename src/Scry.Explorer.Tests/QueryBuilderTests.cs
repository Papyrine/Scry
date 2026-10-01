// The query builder: reading a snippet into rows, writing the rows back, the edits in between, and the
// values a filter compares against. The strongest of these is the last: everything the builder writes
// compiles without a diagnostic in the explorer's own workspace and translates into a wire request.
public class QueryBuilderTests
{
    // Every scalar type the generator emits, a nullable of each kind that has one, a navigation through
    // to another model, and the members a builder has to leave alone: a collection, bulk bytes, an
    // attachment, a sensitive member.
    static readonly ScryIntrospection introspection = new(
        ScryIntrospection.CurrentVersion,
        200,
        [
            new("Item", "Entity", "ItemQueryModel"),
            new("Owner", "Entity", "OwnerQueryModel"),
            new("Asset", "Entity", "AssetQueryModel"),
            new("Building", "Entity", "BuildingQueryModel")
        ],
        [
            new("ItemQueryModel",
            [
                new("Id", "int", false, false),
                new("Name", "string", true, false),
                new("Letter", "char", false, false),
                new("Count", "long", false, false),
                new("Small", "short", false, false),
                new("Tiny", "byte", false, false),
                new("Signed", "sbyte", false, false),
                new("Unsigned", "uint", false, false),
                new("Huge", "ulong", false, false),
                new("Word", "ushort", false, false),
                new("Price", "decimal", false, false),
                new("Ratio", "double", false, false),
                new("Weight", "float", false, false),
                new("Active", "bool", false, false),
                new("Flag", "bool?", false, false),
                new("Status", "Status", false, false),
                new("Previous", "Status?", false, false),
                new("Day", "global::System.DateOnly", false, false),
                new("Moment", "global::System.DateTime", false, false),
                new("Instant", "global::System.DateTimeOffset", false, false),
                new("At", "global::System.TimeOnly", false, false),
                new("Span", "global::System.TimeSpan", false, false),
                new("Key", "global::System.Guid", false, false),
                // BCL enums, which introspection never describes: every client has them already.
                new("Weekday", "global::System.DayOfWeek", false, false),
                new("Rest", "global::System.DayOfWeek?", false, false),
                new("Clock", "global::System.DateTimeKind", false, false),
                new("Rank", "int?", false, false),
                new("Owner", "OwnerQueryModel?", false, true),
                new("Tags", "global::System.Collections.Generic.IReadOnlyList<string>", true, false, true),
                new("Data", "byte[]", true, false),
                new("File", "global::Scry.ScryAttachment", true, false) {IsAttachment = true},
                new("Secret", "string", true, false) {IsSensitive = true}
            ]) {Keys = ["Id"]},
            new("OwnerQueryModel",
            [
                new("Id", "int", false, false),
                new("Name", "string", true, false),
                new("Manager", "OwnerQueryModel?", false, true)
            ]),
            new("AssetQueryModel",
            [
                new("Id", "int", false, false),
                new("Name", "string", true, false)
            ]),
            new("BuildingQueryModel", [new("Floors", "int", false, false)]) {Base = "AssetQueryModel"}
        ],
        [new("Status", ["Open", "Closed"])])
    {
        SchemaStamp = "query-builder"
    };

    static readonly SchemaIndex index = new(introspection);

    static readonly IReadOnlyList<MetadataReference> scryReferences =
    [
        MetadataReference.CreateFromFile(typeof(ScryClient).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(QueryRequest).Assembly.Location)
    ];

    static readonly RoslynWorkspace workspace =
        RoslynWorkspace.Create(ModelSynthesizer.Synthesize(introspection), scryReferences);

    static readonly SnippetExecutor executor = SnippetExecutor.Create(introspection, scryReferences);

    static BuilderQuery Read(string snippet) =>
        QueryBuilder.Read(index, snippet).Query ?? throw new($"Not read: {QueryBuilder.Read(index, snippet).Problem}");

    static string? Problem(string snippet) =>
        QueryBuilder.Read(index, snippet).Problem;

    static string Write(BuilderQuery query) =>
        QueryBuilder.Write(index, query);

    // ---- Reading ----

    [Test]
    public async Task ReadsEveryPartTheBuilderWrites()
    {
        var query = Read(
            """
            Query.Item
                .Where(_ => _.Active && _.Name.Contains("a") && _.Price >= 10m)
                .OrderBy(_ => _.Name)
                .ThenByDescending(_ => _.Owner!.Name)
                .Skip(10)
                .Take(20)
                .Select(_ => new { _.Id, _.Name, Owner = new { _.Owner!.Name } })
                .ToListAsync()
            """);

        using (Assert.Multiple())
        {
            await Assert.That(query.Source).IsEqualTo("Item");
            await Assert.That(Describe(query.Filters)).IsEqualTo("Active IsTrue, Name Contains \"a\", Price GreaterThanOrEqual 10m");
            await Assert.That(query.Orders.Select(_ => $"{string.Join('.', _.Path!)} {_.Descending}")).IsEquivalentTo(["Name False", "Owner.Name True"], CollectionOrdering.Matching);
            await Assert.That(query.Skip).IsEqualTo(10);
            await Assert.That(query.Take).IsEqualTo(20);
            await Assert.That(Describe(query.Columns!)).IsEqualTo("Id, Name, Owner{Name}");
            await Assert.That(query.Terminal).IsEqualTo("ToListAsync");
        }
    }

    // Nothing to read is not a problem: a blank tab is where a new query starts.
    [Test]
    [Arguments("")]
    [Arguments("  \n ")]
    public async Task ReadsNothingFromABlankSnippetAndSaysNothingIsWrong(string snippet)
    {
        var read = QueryBuilder.Read(index, snippet);

        await Assert.That(read.Query).IsNull();
        await Assert.That(read.Problem).IsNull();
    }

    // The explorer opens on a half-typed query, and every query is half-typed while it is being typed.
    [Test]
    public async Task RefusesAQueryThatDoesNotParse() =>
        await Assert.That(Problem("Query.Item.Where(_ => _.")).Contains("does not parse");

    // The query is written back whole, and a comment belongs to no part of it the writer knows of.
    [Test]
    public async Task RefusesAQueryWithAComment() =>
        await Assert.That(Problem("Query.Item\n    // the open ones\n    .Where(_ => _.Active)")).Contains("comment");

    [Test]
    public async Task RefusesAnOperatorItDoesNotWrite() =>
        await Assert.That(Problem("Query.Item.GroupBy(_ => _.Status)")).Contains("GroupBy");

    // Writing them back in the builder's order would page before filtering, which means something else.
    [Test]
    public async Task RefusesOperatorsOutOfTheOrderItWritesThem() =>
        await Assert.That(Problem("Query.Item.Take(5).Where(_ => _.Active)")).Contains("Where");

    [Test]
    public async Task RefusesASecondOrderBy() =>
        await Assert.That(Problem("Query.Item.OrderBy(_ => _.Id).OrderBy(_ => _.Name)")).Contains("OrderBy");

    // A condition kept as code is written back into a lambda with the query's one parameter name.
    [Test]
    public async Task RefusesLambdasThatNameTheirParameterDifferently() =>
        await Assert.That(Problem("Query.Item.Where(x => x.Active).OrderBy(_ => _.Id)")).Contains("parameter");

    [Test]
    public async Task ReadsALambdaParameterOtherThanTheDiscard() =>
        await Assert.That(Read("Query.Item.Where(item => item.Active)").Parameter).IsEqualTo("item");

    [Test]
    public async Task RefusesAQueryNotFromASource() =>
        await Assert.That(Problem("Enumerable.Range(1, 3)")).Contains("source");

    [Test]
    public async Task RefusesASourceTheServerDoesNotPublish() =>
        await Assert.That(Problem("Query.Missing.Where(_ => _.Active)")).Contains("Query.Missing");

    // A terminal taking a predicate filters, and the builder has no row for a filter there.
    [Test]
    public async Task RefusesATerminalWithArguments() =>
        await Assert.That(Problem("Query.Item.FirstAsync(_ => _.Active)")).Contains("FirstAsync");

    [Test]
    public async Task KeepsAConditionItCannotShowAsCode() =>
        await Assert.That(Describe(Read("Query.Item.Where(_ => (_.Active || _.Id > 3) && _.Name == \"x\")").Filters))
            .IsEqualTo("`(_.Active || _.Id > 3)`, Name Equal \"x\"");

    // A value on the left is the same comparison turned around.
    [Test]
    public async Task ReadsAComparisonWrittenTheOtherWayRound() =>
        await Assert.That(Describe(Read("Query.Item.Where(_ => 5 < _.Id)").Filters)).IsEqualTo("Id GreaterThan 5");

    // A nullable member compared with null asks whether it is there; a bool with true, whether it holds.
    [Test]
    public async Task ReadsNullAndBoolComparisonsAsTheirOwnOperators() =>
        await Assert.That(Describe(Read("Query.Item.Where(_ => _.Rank == null && _.Flag == true && _.Owner!.Name != \"x\")").Filters))
            .IsEqualTo("Rank IsNull, Flag IsTrue, Owner.Name NotEqual \"x\"");

    [Test]
    public async Task ReadsWhereCallsInARowAsOneSetOfConditions() =>
        await Assert.That(Read("Query.Item.Where(_ => _.Active).Where(_ => !_.Flag.HasValue)").Filters.Count).IsEqualTo(2);

    // A variable declared ahead of the query is not a literal an input can show, so the condition
    // reading it is kept as code — and the declaration is kept as written.
    [Test]
    public async Task KeepsTheDeclarationsAndAConditionReadingThem()
    {
        var query = Read(
            """
            var since = new DateOnly(2026, 1, 1);
            Query.Item.Where(_ => _.Day >= since)
            """);

        await Assert.That(query.Preamble).Contains("var since");
        await Assert.That(query.Filters[0]).IsEqualTo(new CodeFilter("_.Day >= since"));
    }

    [Test]
    public async Task KeepsAProjectionThatIsNotAnObjectAsCode()
    {
        var query = Read("Query.Item.Select(_ => _.Name)");

        await Assert.That(query.Projection).IsEqualTo("_.Name");
        await Assert.That(query.Columns).IsNull();
    }

    [Test]
    public async Task KeepsAComputedColumnAsCode() =>
        await Assert.That(Describe(Read("Query.Item.Select(_ => new { _.Id, Total = _.Price * 2 })").Columns!))
            .IsEqualTo("Id, `Total = _.Price * 2`");

    // ---- Writing ----

    // Reading and writing back is the format button: the same layout, whatever the input's.
    [Test]
    [Arguments("Query.Item.Where(_ => _.Active).OrderBy(_ => _.Name).Take(5).Select(_ => new { _.Id, Owner = new { _.Owner!.Name } })")]
    [Arguments("Query.Item.Where(_ => _.Name.StartsWith(\"a\") && _.Rank != null && !_.Active).CountAsync()")]
    [Arguments("Query.Item.Where(_ => _.Day >= new DateOnly(2026, 1, 31) && _.Status == Status.Closed)")]
    [Arguments("Query.Item.OrderByDescending(_ => _.Price).ThenBy(_ => _.Owner!.Name).Skip(5).Take(5)")]
    [Arguments("var since = new DateOnly(2026, 1, 1);\nQuery.Item.Where(_ => _.Day >= since)")]
    [Arguments("Query.Item")]
    public async Task WritesBackWhatItReadsInTheFormatButtonsLayout(string snippet) =>
        await Assert.That(Write(Read(snippet))).IsEqualTo(QueryPrinter.Format(snippet));

    [Test]
    public async Task WritesTheLayoutTheExplorerUses() =>
        await Assert.That(Write(Read("Query.Item.Where(_ => _.Active).Select(_ => new { _.Id, _.Name })")))
            .IsEqualTo(
                """
                Query.Item
                    .Where(_ => _.Active)
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            _.Name
                        })
                """);

    // || binds looser than the && the builder joins conditions with, so a condition kept as code goes
    // in parentheses beside another rather than changing what it means.
    [Test]
    public async Task ParenthesisesAConditionThatBindsLooserThanAnd()
    {
        var query = new BuilderQuery("Item")
        {
            Filters =
            [
                new CodeFilter("_.Active || _.Id > 3"),
                new ComparisonFilter(["Name"], FilterOperator.Equal, "\"x\"")
            ]
        };

        await Assert.That(Write(query)).Contains("(_.Active || _.Id > 3) && _.Name == \"x\"");
    }

    // A nullable bool is no condition on its own.
    [Test]
    public async Task ComparesANullableBoolWithTheValueItShouldHold() =>
        await Assert.That(
                Write(
                    new("Item")
                    {
                        Filters = [new ComparisonFilter(["Flag"], FilterOperator.IsFalse, null)]
                    }))
            .Contains("_.Flag == false");

    [Test]
    public async Task KeepsTheAwaitAndTheSemicolon() =>
        await Assert.That(Write(Read("await Query.Item.CountAsync();")))
            .IsEqualTo(
                """
                await Query.Item
                    .CountAsync();
                """);

    // ---- Columns ----

    [Test]
    public async Task ProjectsAColumnIntoAQueryThatHadNoSelect()
    {
        var query = QueryBuilder.ToggleColumn(Read("Query.Item"), ["Name"])!;

        await Assert.That(Describe(query.Columns!)).IsEqualTo("Name");
    }

    // Without columns there is no Select, and the server answers with the default projection.
    [Test]
    public async Task TakesOutTheSelectWithItsLastColumn()
    {
        var query = QueryBuilder.ToggleColumn(Read("Query.Item.Select(_ => new { _.Name })"), ["Name"])!;

        await Assert.That(query.Columns).IsNull();
        await Assert.That(Write(query)).IsEqualTo("Query.Item");
    }

    // A row's own columns go ahead of the objects nested in it, as a starter query lays them out.
    [Test]
    public async Task PutsAScalarAheadOfTheNestedObjects() =>
        await Assert.That(Describe(QueryBuilder.ToggleColumn(Read("Query.Item.Select(_ => new { _.Id, Owner = new { _.Owner!.Name } })"), ["Name"])!.Columns!))
            .IsEqualTo("Id, Name, Owner{Name}");

    [Test]
    public async Task ProjectsAMemberThroughANavigationIntoItsObject() =>
        await Assert.That(Describe(QueryBuilder.ToggleColumn(Read("Query.Item.Select(_ => new { _.Id })"), ["Owner", "Name"])!.Columns!))
            .IsEqualTo("Id, Owner{Name}");

    // An empty new { } is not a projection the server accepts.
    [Test]
    public async Task TakesOutANestedObjectWithItsLastColumn() =>
        await Assert.That(Describe(QueryBuilder.ToggleColumn(Read("Query.Item.Select(_ => new { _.Id, Owner = new { _.Owner!.Name } })"), ["Owner", "Name"])!.Columns!))
            .IsEqualTo("Id");

    // A newly nested object carries what a starter query would: the scalars, and not the navigation
    // back out of it.
    [Test]
    public async Task NestsANavigationWithTheScalarsAStarterQueryWouldPick() =>
        await Assert.That(Describe(QueryBuilder.ToggleNested(index, Read("Query.Item.Select(_ => new { _.Id })"), ["Owner"])!.Columns!))
            .IsEqualTo("Id, Owner{Id, Name}");

    [Test]
    public async Task TakesANestedObjectOut() =>
        await Assert.That(Describe(QueryBuilder.ToggleNested(index, Read("Query.Item.Select(_ => new { _.Id, Owner = new { _.Owner!.Name } })"), ["Owner"])!.Columns!))
            .IsEqualTo("Id");

    // The translator projects nested objects one level deep and refuses an object inside one when the
    // query runs, so the builder does not offer to make one — nor to read one as a row it could edit.
    [Test]
    public async Task NestsOneLevelOnly()
    {
        var query = Read("Query.Item.Select(_ => new { Owner = new { _.Owner!.Name, Manager = new { _.Owner!.Manager!.Name } } })");

        using (Assert.Multiple())
        {
            await Assert.That(QueryBuilder.ToggleNested(index, query, ["Owner", "Manager"])).IsNull();
            await Assert.That(QueryBuilder.ToggleColumn(query, ["Owner", "Manager", "Name"])).IsNull();
            await Assert.That(Describe(query.Columns!)).IsEqualTo("Owner{Name, `Manager = new { _.Owner!.Manager!.Name }`}");
        }
    }

    // A column kept as code can still be taken out, from the top level or from a nested object.
    [Test]
    public async Task RemovesAColumnKeptAsCode()
    {
        var query = Read("Query.Item.Select(_ => new { _.Id, Total = _.Price * 2, Owner = new { _.Owner!.Name, Upper = _.Owner!.Name.ToUpper() } })");

        using (Assert.Multiple())
        {
            await Assert.That(Describe(QueryBuilder.RemoveCodeColumn(query, null, "Total = _.Price * 2")!.Columns!))
                .IsEqualTo("Id, Owner{Name, `Upper = _.Owner!.Name.ToUpper()`}");
            await Assert.That(Describe(QueryBuilder.RemoveCodeColumn(query, "Owner", "Upper = _.Owner!.Name.ToUpper()")!.Columns!))
                .IsEqualTo("Id, `Total = _.Price * 2`, Owner{Name}");
            await Assert.That(QueryBuilder.RemoveCodeColumn(query, null, "Missing = 1")).IsNull();
        }
    }

    // ---- Filters and sorts ----

    // Text starts as a contains of nothing, which narrows nothing until something is typed.
    [Test]
    public async Task AddsAFilterOnTheFirstMemberItCanCompare() =>
        await Assert.That(Write(QueryBuilder.AddFilter(index, Read("Query.Owner"))!))
            .IsEqualTo("Query.Owner\n    .Where(_ => _.Id == 0)");

    [Test]
    public async Task AddsASortOnAMemberNotSortedByYet() =>
        await Assert.That(Write(QueryBuilder.AddOrder(index, Read("Query.Owner.OrderBy(_ => _.Id)"))!))
            .IsEqualTo("Query.Owner\n    .OrderBy(_ => _.Id)\n    .ThenBy(_ => _.Name)");

    // A collection is aggregated rather than compared, an attachment has no value, and bulk bytes are
    // nothing to type a value for; a navigation offers its own members, one level through.
    [Test]
    public async Task OffersTheMembersAFilterCanCompare()
    {
        var names = QueryBuilder.Comparable(index, "ItemQueryModel").Select(_ => _.Display).ToList();

        using (Assert.Multiple())
        {
            await Assert.That(names).Contains("Name");
            await Assert.That(names).Contains("Secret");
            await Assert.That(names).Contains("Owner.Name");
            await Assert.That(names).DoesNotContain("Tags");
            await Assert.That(names).DoesNotContain("File");
            await Assert.That(names).DoesNotContain("Data");
            await Assert.That(names).DoesNotContain("Owner.Manager");
            await Assert.That(names.IndexOf("Name")).IsLessThan(names.IndexOf("Owner.Name"));
        }
    }

    [Test]
    public async Task OffersAnInheritedMember() =>
        await Assert.That(QueryBuilder.Comparable(index, "BuildingQueryModel").Select(_ => _.Display)).IsEquivalentTo(["Id", "Name", "Floors"], CollectionOrdering.Matching);

    // What the new model has stays; what it does not, and anything kept as code, goes.
    [Test]
    public async Task KeepsWhatStillMeansSomethingOnAnotherSource()
    {
        var moved = QueryBuilder.ChangeSource(
            index,
            Read("Query.Item.Where(_ => _.Name == \"a\" && _.Active && (_.Id > 1 || _.Id < 0)).OrderBy(_ => _.Id).Select(_ => new { _.Id, _.Price, Owner = new { _.Owner!.Name } })"),
            "Owner")!;

        await Assert.That(Write(moved)).IsEqualTo(QueryPrinter.Format("Query.Owner.Where(_ => _.Name == \"a\").OrderBy(_ => _.Id).Select(_ => new { _.Id })"));
    }

    [Test]
    public async Task EditsTheTextAndReportsNoChangeAsNull()
    {
        const string snippet = "Query.Item.Take(5)";

        await Assert.That(QueryBuilder.Edit(index, snippet, _ => _ with {Take = 10})).IsEqualTo("Query.Item\n    .Take(10)");
        await Assert.That(QueryBuilder.Edit(index, QueryPrinter.Format(snippet), _ => _)).IsNull();
        await Assert.That(QueryBuilder.Edit(index, "Query.Item.Where(_ => _.", _ => _)).IsNull();
    }

    // ---- Values ----

    [Test]
    [Arguments("string", "say \"hi\"", "\"say \\\"hi\\\"\"")]
    [Arguments("char", "x", "'x'")]
    [Arguments("int", " -12 ", "-12")]
    [Arguments("long", "9000000000", "9000000000")]
    [Arguments("byte", "255", "255")]
    [Arguments("decimal", "12.50", "12.50m")]
    [Arguments("double", "1.5e3", "1500")]
    [Arguments("float", "0.25", "0.25f")]
    [Arguments("bool", "true", "true")]
    [Arguments("Status?", "Closed", "Status.Closed")]
    [Arguments("global::System.DateOnly", "2026-01-31", "new DateOnly(2026, 1, 31)")]
    [Arguments("global::System.DateTime", "2026-01-31T13:45", "new DateTime(2026, 1, 31, 13, 45, 0)")]
    [Arguments("global::System.DateTime", "2026-01-31T00:00", "new DateTime(2026, 1, 31)")]
    [Arguments("global::System.DateTimeOffset", "2026-01-31T13:45:30", "new DateTimeOffset(2026, 1, 31, 13, 45, 30, TimeSpan.Zero)")]
    [Arguments("global::System.TimeOnly", "07:05", "new TimeOnly(7, 5)")]
    [Arguments("global::System.TimeSpan", "1.02:03:04", "new TimeSpan(1, 2, 3, 4)")]
    [Arguments("global::System.Guid", "6F9619FF-8B86-D011-B42D-00C04FC964FF", "new Guid(\"6f9619ff-8b86-d011-b42d-00c04fc964ff\")")]
    [Arguments("global::System.DayOfWeek", "Thursday", "global::System.DayOfWeek.Thursday")]
    [Arguments("global::System.DayOfWeek?", "Sunday", "global::System.DayOfWeek.Sunday")]
    [Arguments("global::System.DateTimeKind", "Utc", "global::System.DateTimeKind.Utc")]
    public async Task WritesAnInputAsALiteralAndReadsItBack(string type, string input, string literal)
    {
        await Assert.That(QueryBuilder.Literal(index, type, input)).IsEqualTo(literal);

        var shown = QueryBuilder.Display(index, type, literal)!;
        await Assert.That(QueryBuilder.Literal(index, type, shown)).IsEqualTo(literal);
    }

    // What is not a value of the type is refused, rather than written as code that does not compile.
    [Test]
    [Arguments("int", "twelve")]
    [Arguments("int", "3000000000")]
    [Arguments("byte", "-1")]
    [Arguments("char", "xy")]
    [Arguments("decimal", "1e3")]
    [Arguments("bool", "yes")]
    [Arguments("Status", "Pending")]
    [Arguments("global::System.DateOnly", "2026-02-31")]
    [Arguments("global::System.TimeOnly", "25:00")]
    [Arguments("global::System.Guid", "not-a-guid")]
    [Arguments("global::System.DayOfWeek", "Funday")]
    [Arguments("global::System.DayOfWeek", "monday")]
    [Arguments("global::System.DayOfWeek", "1")]
    public async Task RefusesAnInputThatIsNotAValueOfTheType(string type, string input) =>
        await Assert.That(QueryBuilder.Literal(index, type, input)).IsNull();

    // Only a literal an input can show is a value; anything else is code.
    [Test]
    [Arguments("int", "since")]
    [Arguments("global::System.DateOnly", "DateOnly.FromDateTime(DateTime.Today)")]
    [Arguments("global::System.DateOnly", "new DateOnly(2026, 2, 31)")]
    [Arguments("string", "$\"{name}\"")]
    public async Task ShowsNothingForCodeThatIsNotALiteral(string type, string code) =>
        await Assert.That(QueryBuilder.Display(index, type, code)).IsNull();

    // A BCL enum is an enum to the builder though introspection lists no values for it: it offers the
    // type's own, and reads a value back however a snippet names the type.
    [Test]
    [Arguments("global::System.DayOfWeek.Friday")]
    [Arguments("System.DayOfWeek.Friday")]
    [Arguments("DayOfWeek.Friday")]
    public async Task ReadsABclEnumValueHoweverTheTypeIsNamed(string code)
    {
        using (Assert.Multiple())
        {
            await Assert.That(QueryBuilder.Kind(index, "global::System.DayOfWeek?")).IsEqualTo(ValueKind.Enum);
            await Assert.That(QueryBuilder.EnumValues(index, "global::System.DayOfWeek")).IsEquivalentTo(Enum.GetNames<DayOfWeek>(), CollectionOrdering.Matching);
            await Assert.That(QueryBuilder.Display(index, "global::System.DayOfWeek", code)).IsEqualTo("Friday");
            await Assert.That(Describe(Read($"Query.Item.Where(_ => _.Weekday == {code})").Filters)).IsEqualTo($"Weekday Equal {code}");
        }
    }

    [Test]
    public async Task OffersTheComparisonsAType()
    {
        using (Assert.Multiple())
        {
            await Assert.That(QueryBuilder.Operators(index, "string")).Contains(FilterOperator.Contains);
            await Assert.That(QueryBuilder.Operators(index, "int")).Contains(FilterOperator.GreaterThan);
            await Assert.That(QueryBuilder.Operators(index, "int")).DoesNotContain(FilterOperator.IsNull);
            await Assert.That(QueryBuilder.Operators(index, "int?")).Contains(FilterOperator.IsNull);
            await Assert.That(QueryBuilder.Operators(index, "Status")).DoesNotContain(FilterOperator.LessThan);
            await Assert.That(QueryBuilder.Operators(index, "global::System.DayOfWeek?")).IsEquivalentTo([FilterOperator.Equal, FilterOperator.NotEqual, FilterOperator.IsNull, FilterOperator.IsNotNull], CollectionOrdering.Matching);
            await Assert.That(QueryBuilder.Operators(index, "bool")).IsEquivalentTo([FilterOperator.IsTrue, FilterOperator.IsFalse], CollectionOrdering.Matching);
            await Assert.That(QueryBuilder.Operators(index, "byte[]")).IsEmpty();
        }
    }

    // ---- What it writes compiles ----

    // Every comparison on every member a filter can name, every sort, paging, and every column the
    // projection can carry, nested objects included: written, then compiled in the explorer's own
    // workspace with nothing to say about it, then translated into the request it would send.
    [Test]
    public async Task EverythingItWritesCompilesAndTranslates()
    {
        var members = QueryBuilder.Comparable(index, "ItemQueryModel");
        List<BuilderFilter> filters = [];
        foreach (var member in members)
        {
            foreach (var comparison in QueryBuilder.Operators(index, member.Member.TypeDisplay))
            {
                var value = QueryBuilder.TakesValue(comparison) ? QueryBuilder.DefaultValue(index, member.Member.TypeDisplay) : null;
                filters.Add(new ComparisonFilter(member.Path, comparison, value));
            }
        }

        var query = new BuilderQuery("Item")
        {
            Filters = filters,
            Orders = [.. members.Select((_, position) => new BuilderOrder(_.Path, null, position % 2 == 1))],
            Skip = 5,
            Take = 10
        };

        foreach (var member in QueryBuilder.Projectable(index, "ItemQueryModel"))
        {
            query = member.IsNavigation
                ? QueryBuilder.ToggleNested(index, query, [member.Name])!
                : QueryBuilder.ToggleColumn(query, [member.Name])!;
        }

        var snippet = Write(query);
        var diagnostics = await workspace.DiagnoseAsync(snippet);
        await Assert.That(diagnostics.Select(_ => _.Message)).IsEmpty().Because(snippet);

        var request = executor.Translate(snippet);
        await Assert.That(request.Root).IsEqualTo("Item");
        await Assert.That(request.Pipeline.OfType<WhereOp>()).IsNotEmpty();

        // And it reads back as what was written.
        await Assert.That(Write(Read(snippet))).IsEqualTo(snippet);
    }

    // The columns as a short string: a nested object's own in braces, code in backticks.
    static string Describe(IReadOnlyList<BuilderColumn> columns) =>
        string.Join(
            ", ",
            columns.Select(_ => _ switch
            {
                MemberColumn member => member.Member,
                NestedColumn nested => $"{nested.Member}{{{Describe(nested.Columns)}}}",
                CodeColumn code => $"`{code.Code}`",
                _ => "?"
            }));

    // The conditions as a short string — path, operator, value — since a ComparisonFilter's path is a list,
    // which record equality compares by reference; code in backticks.
    static string Describe(IReadOnlyList<BuilderFilter> filters) =>
        string.Join(
            ", ",
            filters.Select(_ => _ switch
            {
                ComparisonFilter {Value: null} member => $"{string.Join('.', member.Path)} {member.Operator}",
                ComparisonFilter member => $"{string.Join('.', member.Path)} {member.Operator} {member.Value}",
                CodeFilter code => $"`{code.Code}`",
                _ => "?"
            }));
}
