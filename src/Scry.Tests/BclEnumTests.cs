/// <summary>
/// Members typed as enums of the base class library (<see cref="Sitting"/>), round-tripped through the
/// server on LocalDB. Every client has these types already, so nothing is re-emitted for them; what
/// has to hold is that a value travels both ways. A <see cref="DayOfWeek"/> constant leaves the client
/// as its number — the same constant is compared against a computed <c>DateTime.DayOfWeek</c>, which
/// the server builds as a number — and the server parses a name or a number against the member's own
/// enum, refuses anything the enum does not define, and answers by name.
/// </summary>
public class BclEnumTests
{
    [Test]
    public async Task FiltersByAMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Sitting>("Sitting")
            .Where(_ => _.DayOfWeek == DayOfWeek.Thursday)
            .Select(_ => new {_.Name})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Midweek"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ProjectsEveryMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Sitting>("Sitting")
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Name, _.DayOfWeek, _.Recess, _.Clock})
            .ToListAsync();

        await Verify(rows);
    }

    [Test]
    public async Task FiltersByACapturedValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var day = DayOfWeek.Saturday;

        var rows = await client.Source<Sitting>("Sitting")
            .Where(_ => _.DayOfWeek == day)
            .Select(_ => new {_.Name})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Weekend"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FiltersTheNullableMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var friday = await client.Source<Sitting>("Sitting")
            .Where(_ => _.Recess == DayOfWeek.Friday)
            .Select(_ => new {_.Name})
            .ToListAsync();
        var none = await client.Source<Sitting>("Sitting")
            .Where(_ => _.Recess == null)
            .Select(_ => new {_.Name, _.Recess})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(friday.Select(_ => _.Name)).IsEquivalentTo(["Midweek"], CollectionOrdering.Matching);
            await Assert.That(none.Select(_ => _.Name)).IsEquivalentTo(["Opening"], CollectionOrdering.Matching);
            await Assert.That(none.Single().Recess).IsNull();
        }
    }

    [Test]
    public async Task OrdersByTheMembers()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var descending = await client.Source<Sitting>("Sitting")
            .OrderByDescending(_ => _.DayOfWeek)
            .Select(_ => new {_.Name})
            .ToListAsync();
        var byClock = await client.Source<Sitting>("Sitting")
            .OrderBy(_ => _.Clock)
            .ThenBy(_ => _.DayOfWeek)
            .Select(_ => new {_.Name})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(descending.Select(_ => _.Name)).IsEquivalentTo(["Weekend", "Midweek", "Opening"], CollectionOrdering.Matching);
            // Unspecified, Utc, Local: the enum's own order, as its numbers are stored.
            await Assert.That(byClock.Select(_ => _.Name)).IsEquivalentTo(["Weekend", "Opening", "Midweek"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task GroupsByAMember()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var groups = await client.Source<Sitting>("Sitting")
            .GroupBy(_ => _.Recess)
            .Select(_ => new {Recess = _.Key, Count = _.Count()})
            .ToListAsync();

        await Verify(groups.OrderBy(_ => _.Recess));
    }

    [Test]
    public async Task ReadsTheCollection()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var containing = await client.Source<Sitting>("Sitting")
            .Where(_ => _.Alternates.Contains(DayOfWeek.Friday))
            .Select(_ => new {_.Name})
            .ToListAsync();
        var counts = await client.Source<Sitting>("Sitting")
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Name, Count = _.Alternates.Count})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(containing.Select(_ => _.Name)).IsEquivalentTo(["Opening"], CollectionOrdering.Matching);
            await Assert.That(counts.Select(_ => _.Count)).IsEquivalentTo([2, 1, 0], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task FiltersByADateTimeKind()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Sitting>("Sitting")
            .Where(_ => _.Clock == DateTimeKind.Local)
            .Select(_ => new {_.Name, _.Clock})
            .ToListAsync();

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Midweek"], CollectionOrdering.Matching);
            await Assert.That(rows.Single().Clock).IsEqualTo(DateTimeKind.Local);
        }
    }

    // The constants exactly as the client writes them. A literal compared with any enum member is the
    // number C# lowered it to. A value of the enum's own type — captured, or a collection's element —
    // travels by name, except a DayOfWeek, the one enum that travels as its number everywhere.
    [Test]
    public async Task TheConstantsTheClientSends()
    {
        var client = new ScryClient((_, _) => throw new InvalidOperationException("Only the request is read."));
        var sittings = client.Source<Sitting>("Sitting");
        var day = DayOfWeek.Saturday;
        var clock = DateTimeKind.Utc;

        using (Assert.Multiple())
        {
            await Assert.That(Constant(sittings.Where(_ => _.DayOfWeek == DayOfWeek.Thursday).ToScryRequest())).IsEqualTo(new ConstNode("4", ClrTypeTag.Int32));
            await Assert.That(Constant(sittings.Where(_ => _.DayOfWeek == day).ToScryRequest())).IsEqualTo(new ConstNode("6", ClrTypeTag.Int32));
            await Assert.That(Constant(sittings.Where(_ => _.Alternates.Contains(DayOfWeek.Friday)).ToScryRequest())).IsEqualTo(new ConstNode("5", ClrTypeTag.Int32));
            await Assert.That(Constant(sittings.Where(_ => _.Clock == DateTimeKind.Local).ToScryRequest())).IsEqualTo(new ConstNode("2", ClrTypeTag.Int32));
            await Assert.That(Constant(sittings.Where(_ => _.Clock == clock).ToScryRequest())).IsEqualTo(new ConstNode("Utc", ClrTypeTag.Enum));
        }
    }

    // A captured DateTimeKind, which travels by name, reaches the right rows as surely as a literal.
    [Test]
    public async Task FiltersByACapturedDateTimeKind()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);
        var clock = DateTimeKind.Utc;

        var rows = await client.Source<Sitting>("Sitting")
            .Where(_ => _.Clock == clock)
            .Select(_ => new {_.Name})
            .ToListAsync();

        await Assert.That(rows.Select(_ => _.Name)).IsEquivalentTo(["Opening"], CollectionOrdering.Matching);
    }

    // The server parses the constant against the member's real enum, so a hand-written request may
    // spell the value by name or by number, under either tag — and is answered by name either way.
    [Test]
    [Arguments("Thursday", ClrTypeTag.Enum)]
    [Arguments("Thursday", ClrTypeTag.String)]
    [Arguments("4", ClrTypeTag.Int32)]
    [Arguments("4", ClrTypeTag.Enum)]
    public async Task AcceptsANameOrANumber(string value, ClrTypeTag tag)
    {
        await using var context = TestContext.CreateSeeded();

        var response = SharedProcessor.Instance.Execute(DayRequest(value, tag), context);

        var row = response.Payload.EnumerateArray().Single();
        using (Assert.Multiple())
        {
            await Assert.That(row.GetProperty("name").GetString()).IsEqualTo("Midweek");
            await Assert.That(row.GetProperty("dayOfWeek").GetString()).IsEqualTo("Thursday");
        }
    }

    // A name the enum does not define, a defined name in the wrong case, and a number past its last
    // value are all refused before anything runs, as for a model's own enum.
    [Test]
    [Arguments("Funday", ClrTypeTag.Enum)]
    [Arguments("monday", ClrTypeTag.Enum)]
    [Arguments("7", ClrTypeTag.Int32)]
    [Arguments("-1", ClrTypeTag.Int32)]
    public async Task RejectsAValueTheEnumDoesNotDefine(string value, ClrTypeTag tag)
    {
        await using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(DayRequest(value, tag), context));

        await Assert.That(exception.Message).IsEqualTo($"'{value}' is not a value of enum 'DayOfWeek'.");
    }

    static QueryRequest DayRequest(string value, ClrTypeTag tag) =>
        QueryRequest.Create(
            "Sitting",
            [
                new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["DayOfWeek"]), new ConstNode(value, tag))),
                new SelectOp(new([
                    new("Name", new NodeValue(new MemberNode(["Name"]))),
                    new("DayOfWeek", new NodeValue(new MemberNode(["DayOfWeek"])))
                ]))
            ]);

    // The one constant a single-comparison filter carries, wherever in the request it sits: found in
    // the request as it is serialized, so what is asserted is what crosses the wire.
    internal static ConstNode Constant(QueryRequest request)
    {
        var constants = new List<ConstNode>();
        Collect(JsonSerializer.SerializeToNode(request, ScryJson.Options)!, constants);
        return constants.Single();
    }

    static void Collect(System.Text.Json.Nodes.JsonNode node, List<ConstNode> constants)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject json:
                if (json.TryGetPropertyValue("$type", out var type) &&
                    type?.GetValue<string>() == "const")
                {
                    constants.Add((ConstNode) json.Deserialize<Node>(ScryJson.Options)!);
                    return;
                }

                foreach (var (_, child) in json)
                {
                    if (child is not null)
                    {
                        Collect(child, constants);
                    }
                }

                return;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Collect(child, constants);
                    }
                }

                return;
        }
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
