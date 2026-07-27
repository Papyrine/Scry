namespace Scry.Tests;

[TestFixture]
public class StaleClientTests
{
    // The invalid request in these tests references a property the server does not allow-list —
    // exactly what a deployed client sees after the member is renamed or removed server-side.
    static QueryRequest InvalidRequest(string? stamp) =>
        QueryRequest.Create(
            "Employee",
            [new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Renamed"]), new ConstNode("x", ClrTypeTag.String)))],
            stamp);

    [Test]
    public void MismatchedStampReportsStaleClient()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Processor();

        var exception = Assert.Throws<ScryValidationException>(
            () => processor.Execute(InvalidRequest("stamp-from-an-older-model"), context))!;

        Assert.That(exception.Message, Does.Contain("not allow-listed"));
        Assert.That(exception.Message, Does.Contain("regenerate the client"));
    }

    [Test]
    public void MatchingStampReportsPlainRejection()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Processor();
        var current = processor.Describe().SchemaStamp;

        var exception = Assert.Throws<ScryValidationException>(
            () => processor.Execute(InvalidRequest(current), context))!;

        Assert.That(exception.Message, Does.Contain("not allow-listed"));
        Assert.That(exception.Message, Does.Not.Contain("regenerate the client"));
    }

    [Test]
    public void MissingStampReportsPlainRejection()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Processor();

        var exception = Assert.Throws<ScryValidationException>(
            () => processor.Execute(InvalidRequest(stamp: null), context))!;

        Assert.That(exception.Message, Does.Not.Contain("regenerate the client"));
    }

    // Schema drift alone must not reject anything: a valid query from an outdated client (e.g. after
    // a purely additive model change) still executes.
    [Test]
    public void MismatchedStampWithValidQueryExecutes()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Processor();

        var request = QueryRequest.Create("Employee", [new CountOp()], "stamp-from-an-older-model");
        var response = processor.Execute(request, context);

        Assert.That(response.Kind, Is.EqualTo(ResultKind.Scalar));
    }

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
}
