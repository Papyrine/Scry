/// <summary>
/// The server's schema stamp is carried on every successful response, so drift detection works over any
/// transport. These use an in-process transport — no HTTP, no headers — which is exactly the case the
/// body-carried stamp exists to cover.
/// </summary>
public class ResponseStampTests
{
    // A model frozen at a surface where ManagerId was still non-nullable. Alice has no manager, so a
    // result carrying her cannot be read — drift that no alias can bridge, since nothing was renamed.
    // ReSharper disable NotAccessedPositionalProperty.Local
    record PreNullableEmployee(string Name, int ManagerId);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task EveryResponseCarriesTheServerStamp()
    {
        using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;

        var response = processor.Execute(QueryRequest.Create("Employee", [new CountOp()]), context);

        await Assert.That(response.Stamp).IsEqualTo(processor.Describe().SchemaStamp);
    }

    [Test]
    public async Task StampRoundTripsTheWireAndIsOmittedWhenNull()
    {
        var payload = JsonSerializer.SerializeToElement(1);
        var stamped = QueryResponse.Create(ResultKind.Scalar, payload) with { Stamp = "abc" };

        await Assert.That(ScryJson.DeserializeResponse(ScryJson.Serialize(stamped)).Stamp).IsEqualTo("abc");

        // Additive: a response without the field still deserializes, and none is written when null.
        await Assert.That(ScryJson.Serialize(QueryResponse.Create(ResultKind.Scalar, payload))).DoesNotContain("stamp");
        await Assert.That(ScryJson.DeserializeResponse(
                """
                {
                  "version": 1,
                  "kind": "Scalar",
                  "payload": 1
                }
                """).Stamp).IsNull();
    }

    [Test]
    public async Task DriftIsDetectedOverANonHttpTransport()
    {
        await using var context = TestContext.CreateSeeded();
        var client = StaleClient(context);

        SchemaDrift? drift = null;
        client.SchemaStaleDetected += _ => drift = _;

        // The query succeeds — drift is reported alongside a working result, as with the HTTP header.
        var count = await client.Source<PreNullableEmployee>("Employee", ["Name"]).CountAsync();

        await Assert.That(count).IsEqualTo(4);
        await Assert.That(client.SchemaStale).IsTrue();
        await Assert.That(drift).IsNotNull();
        await Assert.That(drift!.ClientStamp).IsEqualTo("stamp-from-an-older-model");
    }

    [Test]
    public async Task MatchingClientIsNotReportedStaleOverANonHttpTransport()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;
        var client = new ScryClient((request, _) => Task.FromResult(processor.Execute(request, context)))
        {
            SchemaStamp = processor.Describe().SchemaStamp
        };

        var raised = false;
        client.SchemaStaleDetected += _ => raised = true;

        await client.Source<PreNullableEmployee>("Employee", ["Name"]).CountAsync();

        await Assert.That(client.SchemaStale).IsFalse();
        await Assert.That(raised).IsFalse();
    }

    // Payload classification depends on the stamp being recorded before the payload is read. Over a
    // non-HTTP transport the stamp arrives in the same response, so the ordering has to hold there
    // too — this is the in-process counterpart of the HTTP test in IntegrationTests.
    [Test]
    public async Task UnreadablePayloadFromDriftedClientThrowsStaleClientException()
    {
        using var context = TestContext.CreateSeeded();
        var client = StaleClient(context);

        var exception = (await Assert.ThrowsExactlyAsync<ScryStaleClientException>(() =>
            client.Source<PreNullableEmployee>("Employee", ["Name", "ManagerId"]).ToListAsync()))!;

        await Assert.That(exception.Message).Contains("regenerate the client");
        await Assert.That(exception.InnerException).IsAssignableTo<JsonException>();
    }

    static ScryClient StaleClient(TestContext context)
    {
        var processor = SharedProcessor.Instance;
        return new((request, _) => Task.FromResult(processor.Execute(request, context)))
        {
            SchemaStamp = "stamp-from-an-older-model"
        };
    }
}
