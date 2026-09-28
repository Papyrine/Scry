// The house style: the chain down the page, the projection down the page after it, and a predicate
// left on the one line it reads as.
public class QueryPrinterTests
{
    [Test]
    public async Task BreaksTheChainAtEachOperator() =>
        await Assert.That(QueryPrinter.Format("Query.Employee.Where(_ => _.Active).OrderBy(_ => _.Name).ToListAsync()")).IsEqualTo(
                """
                Query.Employee
                    .Where(_ => _.Active)
                    .OrderBy(_ => _.Name)
                    .ToListAsync()
                """);

    [Test]
    public async Task BreaksAFlatProjectionOntoOneLinePerMember() =>
        await Assert.That(QueryPrinter.Format("Query.Employee.Select(_ => new { _.Active, _.Id, _.Name })")).IsEqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Active,
                            _.Id,
                            _.Name
                        })
                """);

    [Test]
    public async Task BreaksANestedProjectionTheSameWay() =>
        await Assert.That(QueryPrinter.Format(
                "Query.Employee.Select(_ => new { _.Id, _.Name, Department = new { _.Department!.Name } })")).IsEqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            _.Name,
                            Department =
                                new
                                {
                                    _.Department!.Name
                                }
                        })
                """);

    [Test]
    public async Task BreaksTwoNestedProjectionsSideBySide() =>
        await Assert.That(QueryPrinter.Format(
                "Query.Employee.Select(_ => new { _.Id, Department = new { _.Department!.Name }, Manager = new { _.Manager!.Name } })")).IsEqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            Department =
                                new
                                {
                                    _.Department!.Name
                                },
                            Manager =
                                new
                                {
                                    _.Manager!.Name
                                }
                        })
                """);

    // A predicate is one thought; stacking it would not make it a clearer one.
    [Test]
    public async Task LeavesAPredicateOnOneLine() =>
        await Assert.That(QueryPrinter.Format("Query.Employee.Where(_ => _.Active && _.Name.StartsWith(\"A\")).ToListAsync()")).IsEqualTo(
                """
                Query.Employee
                    .Where(_ => _.Active && _.Name.StartsWith("A"))
                    .ToListAsync()
                """);

    // Only a projection breaks: a Select onto a single member is not a set of columns.
    [Test]
    public async Task LeavesAScalarSelectOnOneLine() =>
        await Assert.That(QueryPrinter.Format("Query.Employee.Select(_ => _.Name)")).IsEqualTo(
                """
                Query.Employee
                    .Select(_ => _.Name)
                """);

    [Test]
    public async Task LeavesASourceWithNoOperatorsAlone() =>
        await Assert.That(QueryPrinter.Format("Query.Employee")).IsEqualTo("Query.Employee");

    [Test]
    public async Task KeepsTheDeclarationsAheadOfTheQuery() =>
        await Assert.That(QueryPrinter.Format(
                """
                var since = new DateOnly(2026, 1, 1);
                Query.Employee.Where(_ => _.Created >= since).ToListAsync()
                """)).IsEqualTo(
                """
                var since = new DateOnly(2026, 1, 1);

                Query.Employee
                    .Where(_ => _.Created >= since)
                    .ToListAsync()
                """);

    // Comments in the preamble are the caller's own; reformatting the query is no reason to lose them.
    [Test]
    public async Task KeepsACommentInThePreamble() =>
        await Assert.That(QueryPrinter.Format(
                """
                // Everyone hired this year.
                var since = new DateOnly(2026, 1, 1);


                Query.Employee.Where(_ => _.Created >= since)
                """)).IsEqualTo(
                """
                // Everyone hired this year.
                var since = new DateOnly(2026, 1, 1);

                Query.Employee
                    .Where(_ => _.Created >= since)
                """);

    [Test]
    public async Task KeepsATrailingSemicolon() =>
        await Assert.That(QueryPrinter.Format("Query.Employee.Select(_ => _.Name);")).IsEqualTo(
                """
                Query.Employee
                    .Select(_ => _.Name);
                """);

    // Formatting formatted text changes nothing, so the button is safe to lean on.
    [Test]
    [Arguments("Query.Employee.Where(_ => _.Active).ToListAsync()")]
    [Arguments("Query.Employee.Select(_ => new { _.Id, Department = new { _.Department!.Name } })")]
    [Arguments("var since = new DateOnly(2026, 1, 1);\nQuery.Employee.Where(_ => _.Created >= since)")]
    public async Task IsIdempotent(string snippet)
    {
        var once = QueryPrinter.Format(snippet);

        await Assert.That(QueryPrinter.Format(once)).IsEqualTo(once);
    }

    [Test]
    public async Task ReformatsAQueryAlreadySpreadOverLines() =>
        await Assert.That(QueryPrinter.Format(
                """
                Query.Employee
                        .Select(_ => new
                            {
                        _.Id,
                              _.Name })
                """)).IsEqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            _.Name
                        })
                """);

    [Test]
    public async Task ReportsAQueryThatDoesNotParse()
    {
        await Assert.That(QueryPrinter.TryFormat("Query.Employee.Where(_ => ", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("does not parse");
    }

    // ParseExpression stops at the first token it cannot continue from, so trailing garbage would
    // otherwise parse "successfully" as its own prefix — and formatting would silently drop it.
    [Test]
    public async Task ReportsTrailingGarbageRatherThanDroppingIt()
    {
        await Assert.That(QueryPrinter.TryFormat("Query.Employee !! nonsense", out _, out var error)).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task ReportsAnEmptySnippet()
    {
        await Assert.That(QueryPrinter.TryFormat("   ", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("no query");
    }

    // The preamble rule is the snippet layout's, and the format button reports it rather than
    // rewriting around it.
    [Test]
    public async Task ReportsAPreambleThatIsNotADeclaration()
    {
        await Assert.That(QueryPrinter.TryFormat("Console.WriteLine();\nQuery.Employee", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("Only a variable declaration");
    }

    // Format leaves what it cannot read alone, for the callers that compose a query they know parses.
    [Test]
    public async Task FormatReturnsUnreadableTextUnchanged() =>
        await Assert.That(QueryPrinter.Format("Query.Employee.Where(_ => ")).IsEqualTo("Query.Employee.Where(_ => ");
}
