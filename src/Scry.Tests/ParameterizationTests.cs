using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// A client value must reach the database as a bound parameter, not written into the statement. The
/// provider's type mapping would escape an inlined literal correctly, but the statement text would
/// then differ per value — so every value a client sent would compile and cache a plan of its own.
/// </summary>
[TestFixture]
public class ParameterizationTests
{
    [Test]
    public void AClientConstantIsBoundRatherThanWrittenIntoTheStatement()
    {
        var sql = SqlFor(
            QueryRequest.Create(
                "Employee",
                [
                    new WhereOp(new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Name"]),
                        new ConstNode("O'Brien", ClrTypeTag.String))),
                    new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
                ]));

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("@"), "the value is bound");
            Assert.That(sql, Does.Not.Contain("O''Brien"), "and not escaped into the statement text");
        });
    }

    [Test]
    public void TwoValuesProduceTheSameStatement()
    {
        // The point of binding: one plan serves every value, so a client cannot flood the plan cache
        // by varying the values it sends.
        var first = SqlFor(Named("Alice"));
        var second = SqlFor(Named("Carol"));

        Assert.That(Statement(first), Is.EqualTo(Statement(second)));
    }

    static QueryRequest Named(string name) =>
        QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new BinaryNode(
                    BinaryOp.Equal,
                    new MemberNode(["Name"]),
                    new ConstNode(name, ClrTypeTag.String))),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

    [Test]
    public void InValuesAreBoundRatherThanWrittenIntoTheStatement()
    {
        var sql = Statement(SqlFor(NamedIn("Alice", "Bob")));

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("@"), "the values are bound");
            Assert.That(sql, Does.Not.Contain("Alice"), "and not written into the statement text");
        });
    }

    [Test]
    public void TwoInValueSetsProduceTheSameStatement()
    {
        var first = SqlFor(NamedIn("Alice", "Bob"));
        var second = SqlFor(NamedIn("Carol", "Dave"));

        Assert.That(Statement(first), Is.EqualTo(Statement(second)));
    }

    [Test]
    public void TwoInAritiesProduceTheSameStatementWithinABucket()
    {
        // EF binds a parameterized collection as one scalar parameter per value. Tiny lists (up to
        // five values) keep their exact arity; anything larger is padded up to a bucket size by
        // repeating the last value, so nearby arities share the statement text and the database
        // plans behind it. Either way the values stay bound — an inlined array would produce a
        // distinct statement per request.
        var six = SqlFor(NamedIn("A", "B", "C", "D", "E", "F"));
        var eight = SqlFor(NamedIn("A", "B", "C", "D", "E", "F", "G", "H"));

        Assert.That(Statement(six), Is.EqualTo(Statement(eight)));
    }

    static QueryRequest NamedIn(params string[] names) =>
        QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new CallNode(
                    KnownFunction.In,
                    new MemberNode(["Name"]),
                    [..names.Select(_ => new ConstNode(_, ClrTypeTag.String))])),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

    [Test]
    public void TwoSkipTakeValuesProduceTheSameStatement()
    {
        var first = SqlFor(Window(skip: 1, take: 2));
        var second = SqlFor(Window(skip: 5, take: 7));

        Assert.That(Statement(first), Is.EqualTo(Statement(second)));
    }

    static QueryRequest Window(int skip, int take) =>
        QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SkipOp(skip),
                new TakeOp(take),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

    [Test]
    public void TwoPageSizesProduceTheSameStatement()
    {
        var first = SqlFor(Paged(2));
        var second = SqlFor(Paged(3));

        Assert.That(Statement(first), Is.EqualTo(Statement(second)));
    }

    static QueryRequest Paged(int size) =>
        QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new PageOp(size)
            ]);

    // The logged command includes the parameter declaration above the statement; comparing only the
    // statement is what shows the two share a plan.
    static string Statement(string sql) =>
        sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

    static string SqlFor(QueryRequest request)
    {
        using var seeded = TestContext.CreateSeeded();
        var log = new List<string>();
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer(seeded.Database.GetConnectionString())
            .LogTo(log.Add, LogLevel.Information)
            .Options;

        using var logged = new TestContext(options);
        SharedProcessor.Instance.Execute(request, logged);

        return log.Single(_ => _.Contains("SELECT", StringComparison.Ordinal));
    }
}
