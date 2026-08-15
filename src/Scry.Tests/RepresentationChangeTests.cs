/// <summary>
/// A scalar that changes representation server-side — <c>int</c> to <c>string</c>, <c>string</c> to
/// an enum — keeps its name, so [PreviousNames] has nothing to map and the change cannot be bridged.
/// These pin what a client generated against the old type actually experiences, which differs by
/// direction and, on both sides, by the value involved. See docs/schema-versioning.md, "Changing a
/// scalar's representation".
/// </summary>
/// <remarks>
/// The server takes the CLR type from its own schema and never from the wire, so a constant aimed at
/// a member whose type no longer matches it *is* the scenario: Employee.Name is a string and
/// Employee.Id an int, so a numeric constant on Name stands in for a client generated while Name was
/// a number, and a text constant on Id for one generated while Id was text.
/// </remarks>
[TestFixture]
public class RepresentationChangeTests
{
    // A client generated while Employee.Name was still a number. The server's Name is a string, so the
    // row it returns cannot be read back into this shape.
    public class NameAsNumber
    {
        public int Name { get; set; }
    }

    // A client generated while Employee.Status was still free text. An enum serializes as its value
    // name, which is a JSON string either way, so this one reads back without complaint.
    public class StatusAsText
    {
        public string Status { get; set; } = "";
    }

    static int RowCount(string source, string member, BinaryOp op, ConstNode constant)
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create(
            source,
            [new WhereOp(new BinaryNode(op, new MemberNode([member]), constant))]);
        return SharedProcessor.Instance.Execute(request, context).Payload.GetArrayLength();
    }

    static T? ReadRow<T>(string json) =>
        ScryJson.DeserializeRow<T>(JsonSerializer.Deserialize<JsonElement>(json), aliases: null);

    static ScryClient StaleClient(TestContext context)
    {
        var processor = SharedProcessor.Instance;
        return new((request, _) => Task.FromResult(processor.Execute(request, context)))
        {
            SchemaStamp = "stamp-from-an-older-model"
        };
    }

    // Loosening to string, equality: the constant parses as a string trivially and string equality is
    // defined, so the query executes and answers a question nobody asked. This is the one case in the
    // whole compatibility story with no signal on either half of the round trip.
    [Test]
    public void LooseningToStringLeavesEqualitySilent() =>
        Assert.Multiple(() =>
        {
            Assert.That(RowCount("Employee", "Name", BinaryOp.Equal, new("30", ClrTypeTag.Int32)), Is.Zero);
            Assert.That(RowCount("Employee", "Name", BinaryOp.NotEqual, new("30", ClrTypeTag.Int32)), Is.EqualTo(4));
            Assert.That(RowCount("Employee", "Name", BinaryOp.Equal, new("true", ClrTypeTag.Boolean)), Is.Zero);
            Assert.That(RowCount("Employee", "Name", BinaryOp.Equal, new("FullTime", ClrTypeTag.Enum)), Is.Zero);
        });

    // Ordering rescues the same change: string has no relational operator, so the comparison the client
    // meant numerically cannot be built at all and the query is rejected before it runs.
    [Test]
    public void LooseningToStringRejectsOrdering()
    {
        var exception = Assert.Throws<ScryValidationException>(
            () => RowCount("Employee", "Name", BinaryOp.GreaterThan, new("30", ClrTypeTag.Int32)))!;

        Assert.That(exception.Message, Does.Contain("'GreaterThan' is not defined for 'String' and 'String'"));
        Assert.Throws<ScryValidationException>(
            () => RowCount("Employee", "Name", BinaryOp.LessThanOrEqual, new("30", ClrTypeTag.Int32)));
    }

    // Tightening away from string is caught only when the text does not parse in the new type. Every
    // such failure is a rejected query; an enum or char names what the text failed to be...
    [Test]
    public void TighteningRejectsTextThatIsNotAValueOfTheNewType()
    {
        var status = Assert.Throws<ScryValidationException>(
            () => RowCount("Employee", "Status", BinaryOp.Equal, new("Alice", ClrTypeTag.String)))!;
        var grade = Assert.Throws<ScryValidationException>(
            () => RowCount("Order", "Grade", BinaryOp.Equal, new("Alice", ClrTypeTag.String)))!;

        Assert.Multiple(() =>
        {
            Assert.That(status.Message, Does.Contain("is not a value of enum 'Status'"));
            Assert.That(grade.Message, Does.Contain("is not a character"));
        });
    }

    // ...while every other scalar is parsed later, while the expression is being rebound, after
    // validation has already passed — but a value that does not parse is still reported as a rejected
    // query, so the client sees a 400 naming the value rather than an unexplained server fault. For a
    // drifted client the rejection carries the stale-client marker like any other.
    [Test]
    public void TighteningRejectsTextThatDoesNotParseAtRebind() =>
        Assert.Multiple(() =>
        {
            Assert.That(
                Assert.Throws<ScryValidationException>(
                    () => RowCount("Employee", "Id", BinaryOp.Equal, new("Alice", ClrTypeTag.String)))!.Message,
                Does.Contain("is not a valid Int32 value"));
            Assert.That(
                Assert.Throws<ScryValidationException>(
                    () => RowCount("Employee", "Active", BinaryOp.Equal, new("Alice", ClrTypeTag.String)))!.Message,
                Does.Contain("is not a valid Boolean value"));
            Assert.That(
                Assert.Throws<ScryValidationException>(
                    () => RowCount("Order", "Amount", BinaryOp.Equal, new("Alice", ClrTypeTag.String)))!.Message,
                Does.Contain("is not a valid Decimal value"));
            Assert.That(
                Assert.Throws<ScryValidationException>(
                    () => RowCount("Order", "Placed", BinaryOp.Equal, new("Alice", ClrTypeTag.String)))!.Message,
                Does.Contain("is not a valid DateTime value"));
        });

    // The catch: whether tightening is caught depends on the value, not on the change. Text that still
    // parses in the new type sails through, so the same drifted client is loud on one filter and silent
    // on the next.
    [Test]
    public void TighteningIsSilentWhenTheTextStillParses() =>
        Assert.Multiple(() =>
        {
            Assert.That(RowCount("Employee", "Id", BinaryOp.Equal, new("1", ClrTypeTag.String)), Is.EqualTo(1));
            Assert.That(RowCount("Employee", "Active", BinaryOp.Equal, new("true", ClrTypeTag.String)), Is.EqualTo(3));
            Assert.That(RowCount("Employee", "Status", BinaryOp.Equal, new("FullTime", ClrTypeTag.String)), Is.EqualTo(2));
        });

    // Narrowing a numeric is caught the same way, and equally only for values that no longer fit.
    [Test]
    public void NarrowingRejectsAValueThatNoLongerFits()
    {
        var exception = Assert.Throws<ScryValidationException>(
            () => RowCount("Employee", "Id", BinaryOp.Equal, new("99999999999", ClrTypeTag.Int64)))!;
        Assert.That(exception.Message, Does.Contain("is not a valid Int32 value"));

        Assert.That(RowCount("Employee", "Id", BinaryOp.Equal, new("1", ClrTypeTag.Int64)), Is.EqualTo(1));
    }

    // The response half is governed by JSON's token kinds, not by CLR types. A change that crosses from
    // one token kind to another — number to string, bool to string — cannot be read at all, so it is
    // always caught.
    [Test]
    public void ResponseCatchesACrossedJsonTokenKind() =>
        Assert.Multiple(() =>
        {
            Assert.Throws<JsonException>(() => ReadRow<NameAsNumber>("""{"name":"Alice"}"""));
            Assert.Throws<JsonException>(() => ReadRow<StatusAsText>("""{"status":30}"""));
            Assert.Throws<JsonException>(() => ReadRow<StatusAsText>("""{"status":true}"""));
        });

    // Within one token kind there is nothing to notice. An enum, a Guid and a DateTime are all JSON
    // strings, so retyping between them and string reads back clean — the widest silent gap on the
    // response side, and the reason the response half cannot be relied on to catch a retype.
    [Test]
    public void ResponseIsSilentWithinOneJsonTokenKind() =>
        Assert.Multiple(() =>
        {
            Assert.That(ReadRow<StatusAsText>("""{"status":"FullTime"}""")!.Status, Is.EqualTo("FullTime"));
            Assert.That(ReadRow<StatusAsText>("""{"status":"6f9619ff-8b86-d011-b42d-00c04fc964ff"}""")!.Status, Is.EqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
            Assert.That(ReadRow<StatusAsText>("""{"status":"2026-03-04T09:30:15"}""")!.Status, Is.EqualTo("2026-03-04T09:30:15"));
            Assert.That(ReadRow<NameAsNumber>("""{"name":30}""")!.Name, Is.EqualTo(30));
        });

    // Same token kind, but the value no longer fits the new type: caught after all. Like the request
    // half, the signal is value-dependent rather than change-dependent.
    [Test]
    public void ResponseCatchesAValueThatNoLongerFits() =>
        Assert.Multiple(() =>
        {
            Assert.Throws<JsonException>(() => ReadRow<NameAsNumber>("""{"name":99999999999}"""));
            Assert.Throws<JsonException>(() => ReadRow<NameAsNumber>("""{"name":1.5}"""));
        });

    // End to end: when the payload genuinely cannot be read and the stamp already proves the client is
    // behind, the bare JsonException is upgraded to the one exception every stale-client failure shares.
    [Test]
    public void UnreadableRowReportsStaleClient()
    {
        using var context = TestContext.CreateSeeded();
        var client = StaleClient(context);

        var exception = Assert.ThrowsAsync<ScryStaleClientException>(
            () => client.Source<NameAsNumber>("Employee", ["Name"]).ToListAsync())!;

        Assert.That(exception.Message, Does.Contain("could not be read into this client's generated model"));
        Assert.That(exception.Message, Does.Contain("reload the deployed app"));
    }

    // The counterpart, and the one that should worry a reader: the same drifted client reading a
    // retyped member whose JSON token kind did not change gets rows back with no complaint at all.
    [Test]
    public async Task RetypeWithinOneTokenKindReportsNothing()
    {
        await using var context = TestContext.CreateSeeded();
        var client = StaleClient(context);

        var rows = await client.Source<StatusAsText>("Employee", ["Status"]).ToListAsync();

        Assert.That(rows.Select(_ => _.Status), Does.Contain("FullTime"));
    }
}
