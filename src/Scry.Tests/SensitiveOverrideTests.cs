/// <summary>
/// A <c>[Sensitive]</c> marking on a base's virtual property, overridden without the attribute. The
/// generator carries every declaration's attributes onto the one member, so the client refuses the
/// URL and expects the server to; a server reading the override alone accepted the constant in a URL,
/// kept the response storable, and computed a stamp the generator's disagreed with.
/// </summary>
public class SensitiveOverrideTests
{
    [Test]
    public async Task AConstantAgainstTheOverrideIsRefusedFromAUrl()
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Invoice",
            [
                new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Reviewer"]), new ConstNode("Ann", ClrTypeTag.String))),
                new CountOp()
            ]);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Execute(request, context, fromUrl: true, out _));

        await Assert.That(exception.RequiresBody).IsTrue();
    }

    [Test]
    public async Task ReturningTheOverrideIsNotStored()
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Invoice",
            [new SelectOp(new([new("Reviewer", new NodeValue(new MemberNode(["Reviewer"])))]))]);

        Execute(request, context, fromUrl: true, out var headers);

        await Assert.That(headers.CacheControl.ToString()).IsEqualTo("no-store");
    }

    // The unmarked member on the same type keeps the URL: the marking reaches the override, not the type.
    [Test]
    public async Task AnUnmarkedMemberOfTheSameTypeTravelsInTheUrl()
    {
        await using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            "Invoice",
            [
                new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Notes"]), new ConstNode("x", ClrTypeTag.String))),
                new CountOp()
            ]);

        await Assert.That(() => Execute(request, context, fromUrl: true, out _)).ThrowsNothing();
    }

    static QueryResponse Execute(QueryRequest request, TestContext context, bool fromUrl, out IHeaderDictionary responseHeaders)
    {
        responseHeaders = new HeaderDictionary();
        return SharedProcessor.Instance.Execute(
            request,
            context,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            responseHeaders,
            binary: null,
            fromUrl);
    }
}
