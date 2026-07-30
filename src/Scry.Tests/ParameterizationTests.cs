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
