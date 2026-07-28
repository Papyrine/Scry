namespace Scry.Tests;

/// <summary>
/// Covers [PreviousNames]: the names a client generated before a rename keeps sending, which the
/// server still resolves. The test model renames a source (Ticket, was 'Issue'), a member
/// (Employee.Name, was 'FullName'), and an enum value (Status.Contractor, was 'Freelancer').
/// </summary>
[TestFixture]
public class PreviousNamesTests
{
    [Test]
    public void PreviousSourceNameResolves()
    {
        var request = QueryRequest.Create("Issue", [new OrderByOp(new MemberNode(["Name"]), false)]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(Processor().Execute(request, context));

        Assert.That(json, Does.Contain("Login bug"));
        // The source resolves to the same ScrySource, so its row policy still applies.
        Assert.That(json, Does.Not.Contain("Old typo"));
    }

    [Test]
    public void PreviousMemberNameResolvesInFilter()
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
        var json = ScryJson.Serialize(Processor().Execute(request, context));

        Assert.That(json, Does.Contain("Alice"));
        Assert.That(json, Does.Not.Contain("Carol"));
    }

    // A projection keys the response off the name the client asked for, so an old client gets the
    // shape it was generated for even though the member has been renamed server-side.
    [Test]
    public void PreviousMemberNameResolvesInProjection()
    {
        var request = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["FullName"]), false),
                new SelectOp(new([new("FullName", new NodeValue(new MemberNode(["FullName"])))]))
            ]);

        using var context = TestContext.CreateSeeded();
        var json = ScryJson.Serialize(Processor().Execute(request, context));

        Assert.That(json, Does.Contain("\"fullName\""));
        Assert.That(json, Does.Contain("Alice"));
    }

    [Test]
    public void PreviousEnumValueResolves()
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
        var json = ScryJson.Serialize(Processor().Execute(request, context));

        // Carol is the only Contractor.
        Assert.That(json, Does.Contain("Carol"));
        Assert.That(json, Does.Not.Contain("Alice"));
    }

    // An enum value the server has never heard of is a rejected query, not a server fault — the
    // shape a stale client hits when a value was renamed without a [PreviousNames] entry.
    [Test]
    public void UnknownEnumValueIsRejected()
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

        var exception = Assert.Throws<ScryValidationException>(
            () => Processor().Execute(request, context))!;

        Assert.That(exception.Message, Does.Contain("'Departed' is not a value of enum 'Status'"));
    }

    // Previous names are a server-side compatibility affordance, not part of the surface. Leaking
    // them into introspection would put them in generated clients and in the schema stamp, which
    // would defeat drift detection — the rename would stop registering as a change.
    [Test]
    public void PreviousNamesAreExcludedFromIntrospection()
    {
        var introspection = Processor().Describe();

        Assert.That(introspection.Sources.Select(_ => _.Name), Does.Not.Contain("Issue"));
        Assert.That(introspection.Sources.Select(_ => _.Name), Does.Not.Contain("SalesRegion"));

        var employee = introspection.Types.Single(_ => _.Model == "EmployeeQueryModel");
        Assert.That(employee.Members.Select(_ => _.Name), Does.Not.Contain("FullName"));

        var status = introspection.Enums.Single(_ => _.Name == "Status");
        Assert.That(status.Values, Does.Not.Contain("Freelancer"));
    }

    // A previous name that is not registered stays an ordinary rejection, so the allow-list is not
    // widened by anything other than the declared names.
    [Test]
    public void UnregisteredPreviousNameIsStillRejected()
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

        var exception = Assert.Throws<ScryValidationException>(
            () => Processor().Execute(request, context))!;

        Assert.That(exception.Message, Does.Contain("'Surname' is not allow-listed"));
    }

    static ScryProcessor Processor() =>
        ScryProcessor.Create<TestContext>(options => options.AddPocoSource<Holiday>(_ => Holiday.Seed()));
}
