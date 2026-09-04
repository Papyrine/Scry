// The schema pane's data model: the introspection contract arranged for browsing. The type-display
// spellings below are the real ones the server publishes — see
// samples/Sample.Tests/UiSnapshotTests.ExplorerIntrospectionEndpoint.verified.txt.
[TestFixture]
public class SchemaIndexTests
{
    [Test]
    public void FindsASourcesModel()
    {
        var index = Build();

        Assert.That(index.SourceFor("EmployeeQueryModel")!.Name, Is.EqualTo("Employee"));
    }

    // A model only reachable as a navigation is queryable through nothing, and the pane says so by
    // omitting the "queryable as" line.
    [Test]
    public void ReportsNoSourceForAModelNoSourceNames()
    {
        var index = Build();

        Assert.That(index.SourceFor("AssetQueryModel"), Is.Null);
    }

    // Mirrors the walk the generated code is synthesized from, so what the pane lists is what a
    // client would get.
    [Test]
    public void ListsInheritedMembersFirst()
    {
        var index = Build();

        var members = index.AllMembers("BuildingQueryModel");

        Assert.That(members.Select(_ => _.Member.Name), Is.EqualTo(["Id", "Name", "Floors"]));
        Assert.That(members[0].DeclaringModel, Is.EqualTo("AssetQueryModel"));
        Assert.That(members[2].DeclaringModel, Is.EqualTo("BuildingQueryModel"));
    }

    [Test]
    public void LinksABaseModelDownToWhatInheritsIt()
    {
        var index = Build();

        Assert.That(index.Derived("AssetQueryModel"), Is.EqualTo(["BuildingQueryModel", "VehicleQueryModel"]));
    }

    [Test]
    public void ReportsNothingDerivedFromALeaf()
    {
        var index = Build();

        Assert.That(index.Derived("EmployeeQueryModel"), Is.Empty);
    }

    [TestCase("int", "int", null)]
    [TestCase("string", "string", null)]
    [TestCase("int?", "int?", null)]
    [TestCase("byte[]", "byte[]", null)]
    [TestCase("global::System.DateOnly", "System.DateOnly", null)]
    [TestCase("global::Scry.ScryAttachment", "Scry.ScryAttachment", null)]
    public void ResolvesAScalarWithoutALink(string display, string expected, string? target)
    {
        var reference = Build().Resolve(display);

        Assert.That(reference.Display, Is.EqualTo(expected));
        Assert.That(reference.LinkTarget, Is.EqualTo(target));
    }

    [Test]
    public void LinksANavigationToItsModel()
    {
        var reference = Build().Resolve("DepartmentQueryModel?");

        Assert.That(reference.Display, Is.EqualTo("DepartmentQueryModel?"));
        Assert.That(reference.LinkTarget, Is.EqualTo("DepartmentQueryModel"));
    }

    [Test]
    public void LinksAnEnumToItsValues()
    {
        var reference = Build().Resolve("Status");

        Assert.That(reference.LinkTarget, Is.EqualTo("Status"));
    }

    // A collection is shown as what it holds, so the link goes to the model rather than to the list.
    [Test]
    public void UnwrapsACollectionToWhatItHolds()
    {
        var reference = Build().Resolve("global::System.Collections.Generic.IReadOnlyList<EmployeeQueryModel>");

        Assert.That(reference.Display, Is.EqualTo("EmployeeQueryModel[]"));
        Assert.That(reference.LinkTarget, Is.EqualTo("EmployeeQueryModel"));
    }

    [Test]
    public void UnwrapsACollectionOfScalars()
    {
        var reference = Build().Resolve("global::System.Collections.Generic.IReadOnlyList<string>");

        Assert.That(reference.Display, Is.EqualTo("string[]"));
        Assert.That(reference.LinkTarget, Is.Null);
    }

    [Test]
    public void SearchesModelAndMemberNames()
    {
        var matches = Build().Search("Depart");

        Assert.That(matches.Select(_ => $"{_.Model}.{_.Member}"), Does.Contain("EmployeeQueryModel.Department"));
        Assert.That(matches.Any(_ => _ is {Model: "DepartmentQueryModel", Member: null}));
    }

    [Test]
    public void SearchesCaseInsensitively() =>
        Assert.That(Build().Search("depart"), Is.Not.Empty);

    // A search made while reading a type answers about that type before the rest of the schema.
    [Test]
    public void PutsMatchesInsideTheOpenTypeFirst()
    {
        var matches = Build().Search("Name", within: "OrderQueryModel");

        Assert.That(matches[0].Model, Is.EqualTo("OrderQueryModel"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void SearchesNothingForABlankTerm(string? term) =>
        Assert.That(Build().Search(term), Is.Empty);

    // A starter query has to be one the server will run, which is more than "it compiles": a
    // projection carries scalars and objects projected into navigations, and nothing else.
    [Test]
    public void BuildsAStarterQueryOverScalarsAndNavigations() =>
        Assert.That(
            Build().StarterQuery(Build().SourceFor("EmployeeQueryModel")!),
            Is.EqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            _.Name,
                            _.Status,
                            Department =
                                new
                                {
                                    _.Department!.Id,
                                    _.Department!.Name
                                }
                        })
                """));

    // Password is sensitive and Photo is an attachment; neither belongs in a suggested query, for
    // different reasons.
    [Test]
    public void LeavesSensitiveAndAttachmentMembersOutOfAStarterQuery()
    {
        var query = Build().StarterQuery(Build().SourceFor("EmployeeQueryModel")!);

        Assert.That(query, Does.Not.Contain("Password"));
        Assert.That(query, Does.Not.Contain("Photo"));
    }

    // A collection is published with IsNavigation false, so it needs excluding on its own terms.
    // Missing that produced a query the editor compiled and the server rejected with "Projection
    // member must reference a scalar value."
    [Test]
    public void LeavesACollectionOfValuesOutOfAStarterQuery() =>
        Assert.That(
            Build().StarterQuery(Build().SourceFor("OrderQueryModel")!),
            Is.EqualTo(
                """
                Query.Order
                    .Select(_ =>
                        new
                        {
                            _.Name
                        })
                """));

    // A byte[] is bulk bytes whichever way it travels. The contract publishes no flag for the diverted
    // kind — [BinaryTransfer] deliberately does not change the queryable surface — so the rule is the
    // declared type, and it catches both.
    [Test]
    public void LeavesAByteArrayOutOfAStarterQuery()
    {
        var index = Build();

        Assert.That(index.StarterQuery(index.SourceFor("DepartmentQueryModel")!), Does.Not.Contain("Logo"));

        // And through a navigation, where the same member is reached a second way.
        Assert.That(index.StarterQuery(index.SourceFor("EmployeeQueryModel")!), Does.Not.Contain("Logo"));
    }

    [Test]
    public void LeavesACollectionOfRowsOutOfAStarterQuery() =>
        Assert.That(
            Build().StarterQuery(Build().SourceFor("DepartmentQueryModel")!),
            Is.EqualTo(
                """
                Query.Department
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            _.Name
                        })
                """));

    // The navigation is declared nullable, so reading through it warns without the suppression. A
    // model reached through a non-nullable one takes no '!'.
    [Test]
    public void SuppressesTheNullWarningOnANullableNavigation() =>
        Assert.That(
            Build().StarterQuery(Build().SourceFor("EmployeeQueryModel")!),
            Does.Contain("_.Department!.Name"));

    // One level, so a self-navigation terminates rather than recurring.
    [Test]
    public void NestsOnlyOneLevel()
    {
        var index = new SchemaIndex(
            new(
                1,
                200,
                [new("Employee", "Entity", "EmployeeQueryModel")],
                [
                    new("EmployeeQueryModel",
                    [
                        new("Name", "string", true, false),
                        new("Manager", "EmployeeQueryModel?", false, true)
                    ])
                ],
                []));

        Assert.That(
            index.StarterQuery(index.Sources[0]),
            Is.EqualTo(
                """
                Query.Employee
                    .Select(_ =>
                        new
                        {
                            _.Name,
                            Manager =
                                new
                                {
                                    _.Manager!.Name
                                }
                        })
                """));
    }

    // An empty `new { }` is not a projection the server would accept, so a navigation whose model has
    // no scalar to carry is left out rather than nested empty.
    [Test]
    public void LeavesOutANavigationWithNothingToCarry()
    {
        var index = new SchemaIndex(
            new(
                1,
                200,
                [new("Employee", "Entity", "EmployeeQueryModel")],
                [
                    new("EmployeeQueryModel",
                    [
                        new("Name", "string", true, false),
                        new("Photos", "PhotoQueryModel?", false, true)
                    ]),
                    new("PhotoQueryModel",
                    [
                        new("Image", "global::Scry.ScryAttachment", true, false) {IsAttachment = true}
                    ])
                ],
                []));

        Assert.That(index.StarterQuery(index.Sources[0]), Does.Not.Contain("Photos"));
    }

    [Test]
    public void BuildsABareQueryForASourceWithNothingProjectable()
    {
        var introspection = new ScryIntrospection(
            1,
            200,
            [new("Locked", "Entity", "LockedQueryModel")],
            [new("LockedQueryModel", [new("Secret", "string", true, false) {IsSensitive = true}])],
            []);

        var index = new SchemaIndex(introspection);

        Assert.That(index.StarterQuery(index.Sources[0]), Is.EqualTo("Query.Locked"));
    }

    static SchemaIndex Build() =>
        new(
            new(
                1,
                200,
                [
                    new("Building", "Entity", "BuildingQueryModel"),
                    new("Department", "Entity", "DepartmentQueryModel"),
                    new("Employee", "Entity", "EmployeeQueryModel"),
                    new("Order", "Entity", "OrderQueryModel"),
                    new("Vehicle", "Entity", "VehicleQueryModel")
                ],
                [
                    new("AssetQueryModel",
                    [
                        new("Id", "int", false, false),
                        new("Name", "string", true, false)
                    ]),
                    new("BuildingQueryModel", [new("Floors", "int", false, false)]) {Base = "AssetQueryModel"},
                    new("VehicleQueryModel", [new("Wheels", "int", false, false)]) {Base = "AssetQueryModel"},
                    new("DepartmentQueryModel",
                    [
                        new("Employees", "global::System.Collections.Generic.IReadOnlyList<EmployeeQueryModel>", true, false, true),
                        new("Id", "int", false, false),
                        new("Logo", "byte[]", true, false),
                        new("Name", "string", true, false)
                    ]) {Keys = ["Id"]},
                    new("EmployeeQueryModel",
                    [
                        new("Department", "DepartmentQueryModel?", false, true),
                        new("Id", "int", false, false),
                        new("Name", "string", true, false),
                        new("Password", "string", true, false) {IsSensitive = true},
                        new("Photo", "global::Scry.ScryAttachment", true, false) {IsAttachment = true, ContentType = "image/svg+xml"},
                        new("Status", "Status", false, false)
                    ]) {Keys = ["Id"]},
                    new("OrderQueryModel",
                    [
                        new("Name", "string", true, false),
                        new("Tags", "global::System.Collections.Generic.IReadOnlyList<string>", true, false, true)
                    ])
                ],
                [new("Status", ["FullTime", "PartTime", "Contractor"])]));
}
