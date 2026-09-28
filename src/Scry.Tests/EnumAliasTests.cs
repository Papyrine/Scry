/// <summary>
/// The response side of an enum value rename. The payload always carries the current name; for a
/// drifted client the response also carries <see cref="QueryResponse.EnumAliases"/>, and the client's
/// enum reader resolves a name it does not know to a previous name it does. Server model:
/// Status.Contractor was previously 'Freelancer'.
/// </summary>
public class EnumAliasTests
{
    // Frozen at the surface a client generated before the Freelancer -> Contractor rename saw. Nested
    // so the enum's simple name is still 'Status' (alias entries match on it) without colliding with
    // the live model's Status. Settable so rows deserialize.
    public enum Status
    {
        FullTime,
        PartTime,
        Freelancer
    }

    public class Employee
    {
        public string Name { get; set; } = "";
        public Status Status { get; set; }
    }

    [Test]
    public async Task ResponseCarriesAliasesForDriftedClient()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create("Employee", [new CountOp()], "stamp-from-an-older-model");
        var response = SharedProcessor.Instance.Execute(request, context);

        var alias = response.EnumAliases!.Single();
        using (Assert.Multiple())
        {
            await Assert.That(alias.EnumName).IsEqualTo("Status");
            await Assert.That(alias.ValueName).IsEqualTo("Contractor");
            await Assert.That(alias.PreviousNames).IsEquivalentTo(["Freelancer"], CollectionOrdering.Matching);
        }
    }

    // Value names are hashed into the stamp, so a matching (or absent) stamp proves the client
    // already knows the current names — nothing is sent in the common case.
    [Test]
    public async Task ResponseOmitsAliasesWhenStampMatchesOrIsAbsent()
    {
        using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;

        var matched = processor.Execute(
            QueryRequest.Create("Employee", [new CountOp()], processor.Describe().SchemaStamp),
            context);
        var absent = processor.Execute(QueryRequest.Create("Employee", [new CountOp()]), context);

        using (Assert.Multiple())
        {
            await Assert.That(matched.EnumAliases).IsNull();
            await Assert.That(absent.EnumAliases).IsNull();
        }
    }

    // The full round trip a deployed pre-rename client experiences: it filters by the name it was
    // generated with (request direction, resolved via [PreviousNames]) and materializes the value the
    // server returns as 'Contractor' back into its own Status.Freelancer (response direction, resolved
    // via the alias envelope).
    [Test]
    public async Task StaleClientRoundTripsARenamedEnumValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = StaleClient(context);

        var rows = await client.Source<Employee>("Employee")
            .Where(_ => _.Status == Status.Freelancer)
            .ToListAsync();

        var carol = rows.Single();
        using (Assert.Multiple())
        {
            await Assert.That(carol.Name).IsEqualTo("Carol");
            await Assert.That(carol.Status).IsEqualTo(Status.Freelancer);
        }
    }

    // Without aliases (no stamp -> server sends none), an unknown value name is still reported as a
    // stale client rather than a bare JsonException, so the failure diagnoses itself.
    [Test]
    public async Task UnresolvableEnumValueReportsStaleClient()
    {
        using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;
        var client = new ScryClient((request, _) => Task.FromResult(processor.Execute(request, context)));

        var exception = (await Assert.ThrowsExactlyAsync<ScryStaleClientException>(() =>
            client.Source<Employee>("Employee")
                .Where(_ => _.Status == Status.Freelancer)
                .ToListAsync()))!;

        await Assert.That(exception.Message).Contains("'Contractor' is not a value of enum 'Status'");
        await Assert.That(exception.Message).Contains("regenerate");
    }

    // A scalar terminal once read its payload directly, outside the alias scope a list's reader
    // opens, so a renamed value reached MinAsync and MaxAsync as a bare parse failure.
    [Test]
    public async Task AScalarResolvesAnAliasedValue()
    {
        var client = new ScryClient((_, _) => Task.FromResult(
            QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement("Contractor")) with
            {
                EnumAliases = [new("Status", "Contractor", ["Freelancer"])]
            }));

        var status = await client.Source<Employee>("Employee").MaxAsync(_ => _.Status);

        await Assert.That(status).IsEqualTo(Status.Freelancer);
    }

    [Test]
    public async Task AliasesRoundTripTheWireAndAreOmittedWhenNull()
    {
        var payload = JsonSerializer.SerializeToElement(1);
        var response = QueryResponse.Create(ResultKind.Scalar, payload) with
        {
            EnumAliases = [new("Status", "Contractor", ["Freelancer"])]
        };

        var json = ScryJson.Serialize(response);
        var round = ScryJson.DeserializeResponse(json).EnumAliases!.Single();
        using (Assert.Multiple())
        {
            await Assert.That(round.EnumName).IsEqualTo("Status");
            await Assert.That(round.ValueName).IsEqualTo("Contractor");
            await Assert.That(round.PreviousNames).IsEquivalentTo(["Freelancer"], CollectionOrdering.Matching);
        }

        // Absent aliases add nothing to the wire, and a response written before the field existed
        // still deserializes — the field is additive, not a wire break.
        await Assert.That(ScryJson.Serialize(QueryResponse.Create(ResultKind.Scalar, payload))).DoesNotContain("enumAliases");
        var legacy = ScryJson.DeserializeResponse(
            """
            {
              "version": 1,
              "kind": "Scalar",
              "payload": 1
            }
            """);
        await Assert.That(legacy.EnumAliases).IsNull();
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
