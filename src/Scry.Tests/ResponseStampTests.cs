namespace Scry.Tests;

/// <summary>
/// The server's schema stamp rides on every successful response, so drift detection works over any
/// transport. These use an in-process transport — no HTTP, no headers — which is exactly the case the
/// body-carried stamp exists to cover.
/// </summary>
[TestFixture]
public class ResponseStampTests
{
    // A model frozen at a surface where ManagerId was still non-nullable. Alice has no manager, so a
    // result carrying her cannot be read — drift that no alias can bridge, since nothing was renamed.
    // ReSharper disable NotAccessedPositionalProperty.Local
    record PreNullableEmployee(string Name, int ManagerId);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public void EveryResponseCarriesTheServerStamp()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Processor();

        var response = processor.Execute(QueryRequest.Create("Employee", [new CountOp()]), context);

        Assert.That(response.Stamp, Is.EqualTo(processor.Describe().SchemaStamp));
    }

    [Test]
    public void StampRoundTripsTheWireAndIsOmittedWhenNull()
    {
        var payload = JsonSerializer.SerializeToElement(1);
        var stamped = QueryResponse.Create(ResultKind.Scalar, payload) with { Stamp = "abc" };

        Assert.That(ScryJson.DeserializeResponse(ScryJson.Serialize(stamped)).Stamp, Is.EqualTo("abc"));

        // Additive: a response without the field still deserializes, and none is written when null.
        Assert.That(ScryJson.Serialize(QueryResponse.Create(ResultKind.Scalar, payload)), Does.Not.Contain("stamp"));
        Assert.That(
            ScryJson.DeserializeResponse("""{"version":1,"kind":"Scalar","payload":1}""").Stamp,
            Is.Null);
    }

    [Test]
    public async Task DriftIsDetectedOverANonHttpTransport()
    {
        await using var context = TestContext.CreateSeeded();
        var client = StaleClient(context);

        SchemaDrift? drift = null;
        client.SchemaStaleDetected += _ => drift = _;

        // The query succeeds — drift rides alongside a working result, as with the HTTP header.
        var count = await client.Source<PreNullableEmployee>("Employee", ["Name"]).CountAsync();

        Assert.That(count, Is.EqualTo(4));
        Assert.That(client.SchemaStale, Is.True);
        Assert.That(drift, Is.Not.Null);
        Assert.That(drift!.ClientStamp, Is.EqualTo("stamp-from-an-older-model"));
    }

    [Test]
    public async Task MatchingClientIsNotReportedStaleOverANonHttpTransport()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = Processor();
        var client = new ScryClient((request, _) => Task.FromResult(processor.Execute(request, context)))
        {
            SchemaStamp = processor.Describe().SchemaStamp
        };

        var raised = false;
        client.SchemaStaleDetected += _ => raised = true;

        await client.Source<PreNullableEmployee>("Employee", ["Name"]).CountAsync();

        Assert.That(client.SchemaStale, Is.False);
        Assert.That(raised, Is.False);
    }

    // Payload classification depends on the stamp being recorded before the payload is read. Over a
    // non-HTTP transport the stamp arrives in the same response, so the ordering has to hold there
    // too — this is the in-process counterpart of the HTTP test in IntegrationTests.
    [Test]
    public void UnreadablePayloadFromDriftedClientThrowsStaleClientException()
    {
        using var context = TestContext.CreateSeeded();
        var client = StaleClient(context);

        var exception = Assert.ThrowsAsync<ScryStaleClientException>(() =>
            client.Source<PreNullableEmployee>("Employee", ["Name", "ManagerId"]).ToListAsync())!;

        Assert.That(exception.Message, Does.Contain("regenerate the client"));
        Assert.That(exception.InnerException, Is.InstanceOf<JsonException>());
    }

    static ScryClient StaleClient(TestContext context)
    {
        var processor = Processor();
        return new((request, _) => Task.FromResult(processor.Execute(request, context)))
        {
            SchemaStamp = "stamp-from-an-older-model"
        };
    }

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
}
