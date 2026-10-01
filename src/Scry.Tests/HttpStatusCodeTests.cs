/// <summary>
/// A member typed as <see cref="HttpStatusCode"/> (<see cref="Callback"/>), round-tripped through the
/// server on LocalDB. It is the BCL enum that differs from the others in two ways worth pinning: it
/// lives outside CoreLib, so the server knows it by identity rather than by assembly, and it names some
/// values twice — Redirect and Found are both 302 — so either name has to reach the same rows.
/// </summary>
public class HttpStatusCodeTests
{
    [Test]
    public async Task FiltersByAMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Callback>("Callback")
            .Where(_ => _.Status == HttpStatusCode.ServiceUnavailable)
            .Select(_ => new {_.Url})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Url)).IsEquivalentTo(["https://example.com/busy"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ProjectsEveryMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Callback>("Callback")
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Url, _.Status, _.Retry})
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task FiltersByACapturedValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var status = HttpStatusCode.OK;

        var rows = await client.Source<Callback>("Callback")
            .Where(_ => _.Status == status)
            .Select(_ => new {_.Url})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Url)).IsEquivalentTo(["https://example.com/ok"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FiltersTheNullableMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var retried = await client.Source<Callback>("Callback")
            .Where(_ => _.Retry == HttpStatusCode.OK)
            .Select(_ => new {_.Url})
            .ToListAsync();
        var none = await client.Source<Callback>("Callback")
            .Where(_ => _.Retry == null)
            .Select(_ => new {_.Url})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(retried.Select(_ => _.Url)).IsEquivalentTo(["https://example.com/busy"], CollectionOrdering.Matching);
            await Assert.That(none.Select(_ => _.Url)).IsEquivalentTo(["https://example.com/ok", "https://example.com/moved"], CollectionOrdering.Any);
        }
    }

    // Ordered by the code, as the database stores it: 200, 302, 503.
    [Test]
    public async Task OrdersByTheMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Callback>("Callback")
            .OrderBy(_ => _.Status)
            .Select(_ => new {_.Url})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Url)).IsEquivalentTo(
            ["https://example.com/ok", "https://example.com/moved", "https://example.com/busy"],
            CollectionOrdering.Matching);
    }

    // The row was stored as Found; a client writing Redirect means the same 302, captured or literal.
    [Test]
    public async Task EitherNameOfAnAliasedValueFindsTheRow()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var redirect = HttpStatusCode.Redirect;

        var literal = await client.Source<Callback>("Callback")
            .Where(_ => _.Status == HttpStatusCode.Redirect)
            .Select(_ => new {_.Url})
            .ToListAsync();
        var captured = await client.Source<Callback>("Callback")
            .Where(_ => _.Status == redirect)
            .Select(_ => new {_.Url})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(literal.Select(_ => _.Url)).IsEquivalentTo(["https://example.com/moved"], CollectionOrdering.Matching);
            await Assert.That(captured.Select(_ => _.Url)).IsEquivalentTo(["https://example.com/moved"], CollectionOrdering.Matching);
        }
    }

    // A literal is the number C# lowered it to; a captured value travels by the name the runtime gives
    // it, which for an aliased value is whichever of its names that is.
    [Test]
    public async Task TheConstantsTheClientSends()
    {
        var client = new ScryClient((_, _) => throw new InvalidOperationException("Only the request is read."));
        var callbacks = client.Source<Callback>("Callback");
        var ok = HttpStatusCode.OK;
        var redirect = HttpStatusCode.Redirect;

        using (Assert.Multiple())
        {
            await Assert.That(BclEnumTests.Constant(callbacks.Where(_ => _.Status == HttpStatusCode.NotFound).ToScryRequest())).IsEqualTo(new ConstNode("404", ClrTypeTag.Int32));
            await Assert.That(BclEnumTests.Constant(callbacks.Where(_ => _.Status == ok).ToScryRequest())).IsEqualTo(new ConstNode("OK", ClrTypeTag.Enum));
            await Assert.That(BclEnumTests.Constant(callbacks.Where(_ => _.Status == redirect).ToScryRequest())).IsEqualTo(new ConstNode(redirect.ToString(), ClrTypeTag.Enum));
        }
    }

    // Both names of 302, and its number, under the tags a hand-written request might use.
    [Test]
    [Arguments("Found", ClrTypeTag.Enum)]
    [Arguments("Redirect", ClrTypeTag.Enum)]
    [Arguments("Redirect", ClrTypeTag.String)]
    [Arguments("302", ClrTypeTag.Int32)]
    public async Task AcceptsAnyNameOrTheNumber(string value, ClrTypeTag tag)
    {
        await using var context = TestContext.CreateSeeded();

        var response = SharedProcessor.Instance.Execute(StatusRequest(value, tag), context);

        var row = response.Payload.EnumerateArray().Single();
        await Assert.That(row.GetProperty("url").GetString()).IsEqualTo("https://example.com/moved");
    }

    // 418 is a real status code, but not one the enum names: refused like any undefined value.
    [Test]
    [Arguments("Teapot", ClrTypeTag.Enum)]
    [Arguments("notfound", ClrTypeTag.Enum)]
    [Arguments("418", ClrTypeTag.Int32)]
    public async Task RejectsAValueTheEnumDoesNotDefine(string value, ClrTypeTag tag)
    {
        await using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(StatusRequest(value, tag), context));

        await Assert.That(exception.Message).IsEqualTo($"'{value}' is not a value of enum 'HttpStatusCode'.");
    }

    static QueryRequest StatusRequest(string value, ClrTypeTag tag) =>
        QueryRequest.Create(
            "Callback",
            [
                new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Status"]), new ConstNode(value, tag))),
                new SelectOp(new([
                    new("Url", new NodeValue(new MemberNode(["Url"])))
                ]))
            ]);

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
