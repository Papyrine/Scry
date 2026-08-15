/// <summary>
/// The response side of an enum value rename. The payload always carries the current name; for a
/// drifted client the response also carries <see cref="QueryResponse.EnumAliases"/>, and the client's
/// enum reader resolves a name it does not know to a previous name it does. Server model:
/// Status.Contractor was previously 'Freelancer'.
/// </summary>
[TestFixture]
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
    public void ResponseCarriesAliasesForDriftedClient()
    {
        using var context = TestContext.CreateSeeded();

        var request = QueryRequest.Create("Employee", [new CountOp()], "stamp-from-an-older-model");
        var response = SharedProcessor.Instance.Execute(request, context);

        var alias = response.EnumAliases!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(alias.EnumName, Is.EqualTo("Status"));
            Assert.That(alias.ValueName, Is.EqualTo("Contractor"));
            Assert.That(alias.PreviousNames, Is.EqualTo(["Freelancer"]));
        });
    }

    // Value names are hashed into the stamp, so a matching (or absent) stamp proves the client
    // already knows the current names — nothing is sent in the common case.
    [Test]
    public void ResponseOmitsAliasesWhenStampMatchesOrIsAbsent()
    {
        using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;

        var matched = processor.Execute(
            QueryRequest.Create("Employee", [new CountOp()], processor.Describe().SchemaStamp),
            context);
        var absent = processor.Execute(QueryRequest.Create("Employee", [new CountOp()]), context);

        Assert.Multiple(() =>
        {
            Assert.That(matched.EnumAliases, Is.Null);
            Assert.That(absent.EnumAliases, Is.Null);
        });
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
        Assert.Multiple(() =>
        {
            Assert.That(carol.Name, Is.EqualTo("Carol"));
            Assert.That(carol.Status, Is.EqualTo(Status.Freelancer));
        });
    }

    // Without aliases (no stamp -> server sends none), an unknown value name is still reported as a
    // stale client rather than a bare JsonException, so the failure diagnoses itself.
    [Test]
    public void UnresolvableEnumValueReportsStaleClient()
    {
        using var context = TestContext.CreateSeeded();
        var processor = SharedProcessor.Instance;
        var client = new ScryClient((request, _) => Task.FromResult(processor.Execute(request, context)));

        var exception = Assert.ThrowsAsync<ScryStaleClientException>(() =>
            client.Source<Employee>("Employee")
                .Where(_ => _.Status == Status.Freelancer)
                .ToListAsync())!;

        Assert.That(exception.Message, Does.Contain("'Contractor' is not a value of enum 'Status'"));
        Assert.That(exception.Message, Does.Contain("regenerate"));
    }

    [Test]
    public void AliasesRoundTripTheWireAndAreOmittedWhenNull()
    {
        var payload = JsonSerializer.SerializeToElement(1);
        var response = QueryResponse.Create(ResultKind.Scalar, payload) with
        {
            EnumAliases = [new("Status", "Contractor", ["Freelancer"])]
        };

        var json = ScryJson.Serialize(response);
        var round = ScryJson.DeserializeResponse(json).EnumAliases!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(round.EnumName, Is.EqualTo("Status"));
            Assert.That(round.ValueName, Is.EqualTo("Contractor"));
            Assert.That(round.PreviousNames, Is.EqualTo(["Freelancer"]));
        });

        // Absent aliases add nothing to the wire, and a response written before the field existed
        // still deserializes — the field is additive, not a wire break.
        Assert.That(ScryJson.Serialize(QueryResponse.Create(ResultKind.Scalar, payload)), Does.Not.Contain("enumAliases"));
        var legacy = ScryJson.DeserializeResponse(
            """
            {
              "version": 1,
              "kind": "Scalar",
              "payload": 1
            }
            """);
        Assert.That(legacy.EnumAliases, Is.Null);
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
