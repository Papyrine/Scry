/// <summary>
/// A <c>[Sensitive]</c> member reached through an optional struct complex member. The member travels
/// as a <c>Nullable&lt;T&gt;</c>, and the schema keys the struct by <c>T</c>: a resolver that looked the
/// wrapper up as it stood found nothing and answered that nothing beneath it was marked, which let a
/// constant compared against the member into a URL and a response returning it into a cache.
/// </summary>
public class SensitiveStructTests
{
    [Test]
    public async Task AConstantAgainstAMarkedMemberIsRefusedFromAUrl()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Workstation", "Extension"]),
                        new ConstNode("4471", ClrTypeTag.String))),
                new CountOp()
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Execute(request, context, fromUrl: true, out _));

        using (Assert.Multiple())
        {
            await Assert.That(exception!.RequiresBody).IsTrue();
            await Assert.That(exception.Message).Contains("request body");
        }
    }

    // The same member, sent as a body: accepted, and reading through the Nullable reaches the row.
    [Test]
    public async Task AConstantAgainstAMarkedMemberIsAcceptedAsABody()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Workstation", "Extension"]),
                        new ConstNode("4471", ClrTypeTag.String))),
                new CountOp()
            ]);

        var response = Execute(request, context, fromUrl: false, out _);

        await Assert.That(response.Payload.GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task AnUnmarkedMemberOfTheSameStructTravelsInTheUrl()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Workstation", "Room"]),
                        new ConstNode("1.02", ClrTypeTag.String))),
                new CountOp()
            ]);

        var response = Execute(request, context, fromUrl: true, out _);

        await Assert.That(response.Payload.GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task ReturningAMarkedMemberIsNotStored()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), false),
                new SelectOp(new([new("Extension", new NodeValue(new MemberNode(["Workstation", "Extension"])))]))
            ]);

        var response = Execute(request, context, fromUrl: true, out var responseHeaders);

        var extensions = response.Payload.EnumerateArray()
            .Select(_ => _.GetProperty("extension").GetString())
            .ToList();
        using (Assert.Multiple())
        {
            await Assert.That(responseHeaders.CacheControl.ToString()).IsEqualTo("no-store");
            // Aaron, Alice, Bob, Carol: the two without a workstation read as null.
            await Assert.That(extensions).IsEquivalentTo([null, "4471", "4482", null], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ReturningAnUnmarkedMemberIsStorable()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Room", new NodeValue(new MemberNode(["Workstation", "Room"])))]))]);

        Execute(request, context, fromUrl: true, out var responseHeaders);

        await Assert.That(responseHeaders.CacheControl.ToString()).IsEmpty();
    }

    static QueryResponse Execute(QueryRequest request, TestContext context, bool fromUrl, out IHeaderDictionary responseHeaders)
    {
        responseHeaders = new HeaderDictionary();
        return SharedProcessor.Instance.Execute(
            request,
            context,
            NoServices.Instance,
            new HeaderDictionary(),
            responseHeaders,
            binary: null,
            fromUrl);
    }

    sealed class NoServices :
        IServiceProvider
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) =>
            null;
    }
}
