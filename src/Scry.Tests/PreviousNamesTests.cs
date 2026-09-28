/// <summary>
/// Covers [PreviousNames]: the names a client generated before a rename keeps sending, which the
/// server still resolves. The test model renames a source (Ticket, was 'Issue'), a member
/// (Employee.Name, was 'FullName'), and an enum value (Status.Contractor, was 'Freelancer').
/// </summary>
public class PreviousNamesTests
{
    [Test]
    public async Task PreviousSourceNameResolves()
    {
        var request = QueryRequest.Create("Issue", [new OrderByOp(new MemberNode(["Name"]), false)]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(SharedProcessor.Instance.Execute(request, context));

        await Assert.That(json).Contains("Login bug");
        // The source resolves to the same ScrySource, so its row policy still applies.
        await Assert.That(json).DoesNotContain("Old typo");
    }

    [Test]
    public async Task PreviousMemberNameResolvesInFilter()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["FullName"]),
                        new ConstNode("Alice", ClrTypeTag.String))),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(SharedProcessor.Instance.Execute(request, context));

        await Assert.That(json).Contains("Alice");
        await Assert.That(json).DoesNotContain("Carol");
    }

    // A projection keys the response off the name the client asked for, so an old client gets the
    // shape it was generated for even though the member has been renamed server-side.
    [Test]
    public async Task PreviousMemberNameResolvesInProjection()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["FullName"]), false),
                new SelectOp(new([new("FullName", new NodeValue(new MemberNode(["FullName"])))]))
            ]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(SharedProcessor.Instance.Execute(request, context));

        await Assert.That(json).Contains("\"fullName\"");
        await Assert.That(json).Contains("Alice");
    }

    [Test]
    public async Task PreviousEnumValueResolves()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Status"]),
                        new ConstNode("Freelancer", ClrTypeTag.Enum))),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(SharedProcessor.Instance.Execute(request, context));

        // Carol is the only Contractor.
        await Assert.That(json).Contains("Carol");
        await Assert.That(json).DoesNotContain("Alice");
    }

    // The payload always carries the current name — never the previous one, and never both, since a
    // value has a single string slot. The response direction is instead handled out of band: a drifted
    // client receives QueryResponse.EnumAliases and its reader translates (see EnumAliasTests). Pinned
    // so response-side dual-naming cannot creep into the payload itself.
    [Test]
    public async Task RenamedEnumValueIsReturnedUnderItsCurrentName()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Status"]),
                        new ConstNode("Freelancer", ClrTypeTag.Enum))),
                new SelectOp(new([new("Status", new NodeValue(new MemberNode(["Status"])))]))
            ]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(SharedProcessor.Instance.Execute(request, context));

        // Filtered by the previous name, returned under the current one.
        await Assert.That(json).Contains("Contractor");
        await Assert.That(json).DoesNotContain("Freelancer");
    }

    // An enum value the server has never heard of is a rejected query, not a server fault — the
    // shape a stale client hits when a value was renamed without a [PreviousNames] entry.
    [Test]
    public async Task UnknownEnumValueIsRejected()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Status"]),
                        new ConstNode("Departed", ClrTypeTag.Enum)))
            ]);

        using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("'Departed' is not a value of enum 'Status'");
    }

    // Previous names are a server-side compatibility affordance, not part of the surface. Leaking
    // them into introspection would put them in generated clients and in the schema stamp, which
    // would defeat drift detection — the rename would stop registering as a change.
    [Test]
    public async Task PreviousNamesAreExcludedFromIntrospection()
    {
        var introspection = SharedProcessor.Instance.Describe();

        await Assert.That(introspection.Sources.Select(_ => _.Name)).DoesNotContain("Issue");
        await Assert.That(introspection.Sources.Select(_ => _.Name)).DoesNotContain("SalesRegion");

        var employee = introspection.Types.Single(_ => _.Model == "EmployeeQueryModel");
        await Assert.That(employee.Members.Select(_ => _.Name)).DoesNotContain("FullName");

        var status = introspection.Enums.Single(_ => _.Name == "Status");
        await Assert.That(status.Values).DoesNotContain("Freelancer");
    }

    // A previous name that is not registered stays an ordinary rejection, so the allow-list is not
    // widened by anything other than the declared names.
    [Test]
    public async Task UnregisteredPreviousNameIsStillRejected()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Surname"]),
                        new ConstNode("Alice", ClrTypeTag.String)))
            ]);

        using var context = TestContext.CreateSeeded();

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.Execute(request, context))!;

        await Assert.That(exception.Message).Contains("'Surname' is not allow-listed");
    }

}
