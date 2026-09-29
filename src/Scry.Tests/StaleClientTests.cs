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
    public async Task MismatchedStampReportsStaleClient()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => processor.Execute(InvalidRequest("stamp-from-an-older-model"), context));

        await Assert.That(exception.Message).Contains("not allow-listed");
        await Assert.That(exception.Message).Contains("regenerate the client");
        // The structured counterpart of the prose: the HTTP endpoint forwards it as
        // ScryError.StaleClient so client code can prompt a reload without parsing messages.
        await Assert.That(exception.StaleClient).IsTrue();
    }

    [Test]
    public async Task MatchingStampReportsPlainRejection()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;
        var current = processor.Describe().SchemaStamp;

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => processor.Execute(InvalidRequest(current), context));

        await Assert.That(exception.Message).Contains("not allow-listed");
        await Assert.That(exception.Message).DoesNotContain("regenerate the client");
        await Assert.That(exception.StaleClient).IsFalse();
    }

    [Test]
    public async Task MissingStampReportsPlainRejection()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => processor.Execute(InvalidRequest(stamp: null), context));

        await Assert.That(exception.Message).DoesNotContain("regenerate the client");
        await Assert.That(exception.StaleClient).IsFalse();
    }

    // Schema drift alone must not reject anything: a valid query from an outdated client (e.g. after
    // a purely additive model change) still executes.
    [Test]
    public async Task MismatchedStampWithValidQueryExecutes()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;

        var request = QueryRequest.Create("Employee", [new CountOp()], "stamp-from-an-older-model");
        var response = processor.Execute(request, context);

        await Assert.That(response.Kind).IsEqualTo(ResultKind.Scalar);
    }
}