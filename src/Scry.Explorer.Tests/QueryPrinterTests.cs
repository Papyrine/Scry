// The house style: the chain down the page, the projection down the page after it, and a predicate
// left on the one line it reads as.
[TestFixture]
public class QueryPrinterTests
{
    [Test]
    public void BreaksTheChainAtEachOperator() =>
        Assert.That(
            QueryPrinter.Format("Query.Employee.Where(_ => _.Active).OrderBy(_ => _.Name).ToListAsync()"),
            Is.EqualTo(
                """
                Query.Employee
                    .Where(_ => _.Active)
                    .OrderBy(_ => _.Name)
                    .ToListAsync()
                """));

    [Test]
    public void BreaksAFlatProjectionOntoOneLinePerMember() =>
        Assert.That(
            QueryPrinter.Format("Query.Employee.Select(_ => new { _.Active, _.Id, _.Name })"),
            Is.EqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Active,
                            _.Id,
                            _.Name
                        })
                """));

    [Test]
    public void BreaksANestedProjectionTheSameWay() =>
        Assert.That(
            QueryPrinter.Format(
                "Query.Employee.Select(_ => new { _.Id, _.Name, Department = new { _.Department!.Name } })"),
            Is.EqualTo(
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
                """));

    [Test]
    public void BreaksTwoNestedProjectionsSideBySide() =>
        Assert.That(
            QueryPrinter.Format(
                "Query.Employee.Select(_ => new { _.Id, Department = new { _.Department!.Name }, Manager = new { _.Manager!.Name } })"),
            Is.EqualTo(
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
                """));

    // A predicate is one thought; stacking it would not make it a clearer one.
    [Test]
    public void LeavesAPredicateOnOneLine() =>
        Assert.That(
            QueryPrinter.Format("Query.Employee.Where(_ => _.Active && _.Name.StartsWith(\"A\")).ToListAsync()"),
            Is.EqualTo(
                """
                Query.Employee
                    .Where(_ => _.Active && _.Name.StartsWith("A"))
                    .ToListAsync()
                """));

    // Only a projection breaks: a Select onto a single member is not a set of columns.
    [Test]
    public void LeavesAScalarSelectOnOneLine() =>
        Assert.That(
            QueryPrinter.Format("Query.Employee.Select(_ => _.Name)"),
            Is.EqualTo(
                """
                Query.Employee
                    .Select(_ => _.Name)
                """));

    [Test]
    public void LeavesASourceWithNoOperatorsAlone() =>
        Assert.That(QueryPrinter.Format("Query.Employee"), Is.EqualTo("Query.Employee"));

    [Test]
    public void KeepsTheDeclarationsAheadOfTheQuery() =>
        Assert.That(
            QueryPrinter.Format(
                """
                var since = new DateOnly(2026, 1, 1);
                Query.Employee.Where(_ => _.Created >= since).ToListAsync()
                """),
            Is.EqualTo(
                """
                var since = new DateOnly(2026, 1, 1);

                Query.Employee
                    .Where(_ => _.Created >= since)
                    .ToListAsync()
                """));

    // Comments in the preamble are the caller's own; reformatting the query is no reason to lose them.
    [Test]
    public void KeepsACommentInThePreamble() =>
        Assert.That(
            QueryPrinter.Format(
                """
                // Everyone hired this year.
                var since = new DateOnly(2026, 1, 1);


                Query.Employee.Where(_ => _.Created >= since)
                """),
            Is.EqualTo(
                """
                // Everyone hired this year.
                var since = new DateOnly(2026, 1, 1);

                Query.Employee
                    .Where(_ => _.Created >= since)
                """));

    [Test]
    public void KeepsATrailingSemicolon() =>
        Assert.That(
            QueryPrinter.Format("Query.Employee.Select(_ => _.Name);"),
            Is.EqualTo(
                """
                Query.Employee
                    .Select(_ => _.Name);
                """));

    // Formatting formatted text changes nothing, so the button is safe to lean on.
    [TestCase("Query.Employee.Where(_ => _.Active).ToListAsync()")]
    [TestCase("Query.Employee.Select(_ => new { _.Id, Department = new { _.Department!.Name } })")]
    [TestCase("var since = new DateOnly(2026, 1, 1);\nQuery.Employee.Where(_ => _.Created >= since)")]
    public void IsIdempotent(string snippet)
    {
        var once = QueryPrinter.Format(snippet);

        Assert.That(QueryPrinter.Format(once), Is.EqualTo(once));
    }

    [Test]
    public void ReformatsAQueryAlreadySpreadOverLines() =>
        Assert.That(
            QueryPrinter.Format(
                """
                Query.Employee
                        .Select(_ => new
                            {
                        _.Id,
                              _.Name })
                """),
            Is.EqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            _.Name
                        })
                """));

    [Test]
    public void ReportsAQueryThatDoesNotParse()
    {
        Assert.That(QueryPrinter.TryFormat("Query.Employee.Where(_ => ", out _, out var error), Is.False);
        Assert.That(error, Does.Contain("does not parse"));
    }

    // ParseExpression stops at the first token it cannot continue from, so trailing garbage would
    // otherwise parse "successfully" as its own prefix — and formatting would silently drop it.
    [Test]
    public void ReportsTrailingGarbageRatherThanDroppingIt()
    {
        Assert.That(QueryPrinter.TryFormat("Query.Employee !! nonsense", out _, out var error), Is.False);
        Assert.That(error, Is.Not.Null);
    }

    [Test]
    public void ReportsAnEmptySnippet()
    {
        Assert.That(QueryPrinter.TryFormat("   ", out _, out var error), Is.False);
        Assert.That(error, Does.Contain("no query"));
    }

    // The preamble rule is the snippet layout's, and the format button reports it rather than
    // rewriting around it.
    [Test]
    public void ReportsAPreambleThatIsNotADeclaration()
    {
        Assert.That(
            QueryPrinter.TryFormat("Console.WriteLine();\nQuery.Employee", out _, out var error),
            Is.False);
        Assert.That(error, Does.Contain("Only a variable declaration"));
    }

    // Format leaves what it cannot read alone, for the callers that compose a query they know parses.
    [Test]
    public void FormatReturnsUnreadableTextUnchanged() =>
        Assert.That(QueryPrinter.Format("Query.Employee.Where(_ => "), Is.EqualTo("Query.Employee.Where(_ => "));
}
