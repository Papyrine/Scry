// The schema pane's data model: the introspection contract arranged for browsing. The type-display
// spellings below are the real ones the server publishes — see
// samples/Sample.Tests/UiSnapshotTests.ExplorerIntrospectionEndpoint.verified.txt.
public class SchemaIndexTests
{
    [Test]
    public async Task FindsASourcesModel()
    {
        var index = Build();

        await Assert.That(index.SourceFor("EmployeeQueryModel")!.Name).IsEqualTo("Employee");
    }

    // A model only reachable as a navigation is queryable through nothing, and the pane says so by
    // omitting the "queryable as" line.
    [Test]
    public async Task ReportsNoSourceForAModelNoSourceNames()
    {
        var index = Build();

        await Assert.That(index.SourceFor("AssetQueryModel")).IsNull();
    }

    // Mirrors the walk the generated code is synthesized from, so what the pane lists is what a
    // client would get.
    [Test]
    public async Task ListsInheritedMembersFirst()
    {
        var index = Build();

        var members = index.AllMembers("BuildingQueryModel");

        await Assert.That(members.Select(_ => _.Member.Name)).IsEquivalentTo(["Id", "Name", "Floors"], CollectionOrdering.Matching);
        await Assert.That(members[0].DeclaringModel).IsEqualTo("AssetQueryModel");
        await Assert.That(members[2].DeclaringModel).IsEqualTo("BuildingQueryModel");
    }

    [Test]
    public async Task LinksABaseModelDownToWhatInheritsIt()
    {
        var index = Build();

        await Assert.That(index.Derived("AssetQueryModel")).IsEquivalentTo(["BuildingQueryModel", "VehicleQueryModel"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReportsNothingDerivedFromALeaf()
    {
        var index = Build();

        await Assert.That(index.Derived("EmployeeQueryModel")).IsEmpty();
    }

    [Test]
    [Arguments("int", "int", null)]
    [Arguments("string", "string", null)]
    [Arguments("int?", "int?", null)]
    [Arguments("byte[]", "byte[]", null)]
    [Arguments("global::System.DateOnly", "System.DateOnly", null)]
    [Arguments("global::Scry.ScryAttachment", "Scry.ScryAttachment", null)]
    public async Task ResolvesAScalarWithoutALink(string display, string expected, string? target)
    {
        var reference = Build().Resolve(display);

        await Assert.That(reference.Display).IsEqualTo(expected);
        await Assert.That(reference.LinkTarget).IsEqualTo(target);
    }

    [Test]
    public async Task LinksANavigationToItsModel()
    {
        var reference = Build().Resolve("DepartmentQueryModel?");

        await Assert.That(reference.Display).IsEqualTo("DepartmentQueryModel?");
        await Assert.That(reference.LinkTarget).IsEqualTo("DepartmentQueryModel");
    }

    [Test]
    public async Task LinksAnEnumToItsValues()
    {
        var reference = Build().Resolve("Status");

        await Assert.That(reference.LinkTarget).IsEqualTo("Status");
    }

    // A collection is shown as what it holds, so the link goes to the model rather than to the list.
    [Test]
    public async Task UnwrapsACollectionToWhatItHolds()
    {
        var reference = Build().Resolve("global::System.Collections.Generic.IReadOnlyList<EmployeeQueryModel>");

        await Assert.That(reference.Display).IsEqualTo("EmployeeQueryModel[]");
        await Assert.That(reference.LinkTarget).IsEqualTo("EmployeeQueryModel");
    }

    [Test]
    public async Task UnwrapsACollectionOfScalars()
    {
        var reference = Build().Resolve("global::System.Collections.Generic.IReadOnlyList<string>");

        await Assert.That(reference.Display).IsEqualTo("string[]");
        await Assert.That(reference.LinkTarget).IsNull();
    }

    [Test]
    public async Task SearchesModelAndMemberNames()
    {
        var matches = Build().Search("Depart");

        await Assert.That(matches.Select(_ => $"{_.Model}.{_.Member}")).Contains("EmployeeQueryModel.Department");
        await Assert.That(matches.Any(_ => _ is {Model: "DepartmentQueryModel", Member: null})).IsTrue();
    }

    [Test]
    public async Task SearchesCaseInsensitively() =>
        await Assert.That(Build().Search("depart")).IsNotEmpty();

    // A search made while reading a type answers about that type before the rest of the schema.
    [Test]
    public async Task PutsMatchesInsideTheOpenTypeFirst()
    {
        var matches = Build().Search("Name", within: "OrderQueryModel");

        await Assert.That(matches[0].Model).IsEqualTo("OrderQueryModel");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task SearchesNothingForABlankTerm(string? term) =>
        await Assert.That(Build().Search(term)).IsEmpty();

    // A starter query has to be one the server will run, which is more than "it compiles": a
    // projection carries scalars and objects projected into navigations, and nothing else.
    [Test]
    public async Task BuildsAStarterQueryOverScalarsAndNavigations() =>
        await Assert.That(Build().StarterQuery(Build().SourceFor("EmployeeQueryModel")!)).IsEqualTo(
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
                """);

    // Password is sensitive and Photo is an attachment; neither belongs in a suggested query, for
    // different reasons.
    [Test]
    public async Task LeavesSensitiveAndAttachmentMembersOutOfAStarterQuery()
    {
        var query = Build().StarterQuery(Build().SourceFor("EmployeeQueryModel")!);

        await Assert.That(query).DoesNotContain("Password");
        await Assert.That(query).DoesNotContain("Photo");
    }

    // A collection is published with IsNavigation false, so it needs excluding on its own terms.
    // Missing that produced a query the editor compiled and the server rejected with "Projection
    // member must reference a scalar value."
    [Test]
    public async Task LeavesACollectionOfValuesOutOfAStarterQuery() =>
        await Assert.That(Build().StarterQuery(Build().SourceFor("OrderQueryModel")!)).IsEqualTo(
                """
                Query.Order
                    .Select(_ =>
                        new
                        {
                            _.Name
                        })
                """);

    // A byte[] is bulk bytes whichever way it travels. The contract publishes no flag for the diverted
    // kind — [BinaryTransfer] deliberately does not change the queryable surface — so the rule is the
    // declared type, and it catches both.
    [Test]
    public async Task LeavesAByteArrayOutOfAStarterQuery()
    {
        var index = Build();

        await Assert.That(index.StarterQuery(index.SourceFor("DepartmentQueryModel")!)).DoesNotContain("Logo");

        // And through a navigation, where the same member is reached a second way.
        await Assert.That(index.StarterQuery(index.SourceFor("EmployeeQueryModel")!)).DoesNotContain("Logo");
    }

    [Test]
    public async Task LeavesACollectionOfRowsOutOfAStarterQuery() =>
        await Assert.That(Build().StarterQuery(Build().SourceFor("DepartmentQueryModel")!)).IsEqualTo(
                """
                Query.Department
                    .Select(_ =>
                        new
                        {
                            _.Id,
                            _.Name
                        })
                """);

    // The navigation is declared nullable, so reading through it warns without the suppression. A
    // model reached through a non-nullable one takes no '!'.
    [Test]
    public async Task SuppressesTheNullWarningOnANullableNavigation() =>
        await Assert.That(Build().StarterQuery(Build().SourceFor("EmployeeQueryModel")!)).Contains("_.Department!.Name");

    // One level, so a self-navigation terminates rather than recurring.
    [Test]
    public async Task NestsOnlyOneLevel()
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

        await Assert.That(index.StarterQuery(index.Sources[0])).IsEqualTo(
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
                """);
    }

    // An empty `new { }` is not a projection the server would accept, so a navigation whose model has
    // no scalar to carry is left out rather than nested empty.
    [Test]
    public async Task LeavesOutANavigationWithNothingToCarry()
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

        await Assert.That(index.StarterQuery(index.Sources[0])).DoesNotContain("Photos");
    }

    // A command acts on its target's rows and on the rows of anything deriving from it, which inherits
    // the capability; the others are listed only on the pane's first page.
    [Test]
    public async Task ListsCommandsTargetingAModel()
    {
        var index = Commanded();

        using (Assert.Multiple())
        {
            await Assert.That(index.CommandsTargeting("AssetQueryModel").Select(_ => _.Name)).IsEquivalentTo(["Retire"], CollectionOrdering.Matching);
            await Assert.That(index.CommandsTargeting("VehicleQueryModel").Select(_ => _.Name)).IsEquivalentTo(["Retire"], CollectionOrdering.Matching);
            await Assert.That(index.CommandsTargeting("DepotQueryModel")).IsEmpty();
            await Assert.That(index.Command("Order")!.Result!.Name).IsEqualTo("Ordered");
        }
    }

    // A capability projects like any bool, but it is the command's policy run per row — a cost a
    // suggested query should not open with.
    [Test]
    public async Task LeavesCapabilitiesOutOfAStarterQuery()
    {
        var index = Commanded();

        var query = index.StarterQuery(index.Sources.Single(_ => _.Name == "Asset"));

        using (Assert.Multiple())
        {
            await Assert.That(query).Contains("_.Name");
            await Assert.That(query).DoesNotContain("CanRetire");
        }
    }

    static SchemaIndex Commanded() =>
        new(
            new(
                2,
                200,
                [
                    new("Asset", "Entity", "AssetQueryModel"),
                    new("Vehicle", "Entity", "VehicleQueryModel"),
                    new("Depot", "Entity", "DepotQueryModel")
                ],
                [
                    new("AssetQueryModel",
                    [
                        new("Id", "int", false, false),
                        new("Name", "string", true, false),
                        new("CanRetire", "bool", false, false)
                        {
                            IsCapability = true,
                            Command = "Retire"
                        }
                    ]),
                    new("VehicleQueryModel", [new("Wheels", "int", false, false)]) {Base = "AssetQueryModel"},
                    new("DepotQueryModel", [new("Id", "int", false, false)])
                ],
                [])
            {
                Commands =
                [
                    new("Order", [new("Name", "string", true, false)])
                    {
                        Result = new("Ordered", [new("Id", "int", false, false)])
                    },
                    new("Retire", [new("Id", "int", false, false)])
                    {
                        Target = "Asset",
                        Keys = ["Id"]
                    }
                ]
            });

    [Test]
    public async Task BuildsABareQueryForASourceWithNothingProjectable()
    {
        var introspection = new ScryIntrospection(
            1,
            200,
            [new("Locked", "Entity", "LockedQueryModel")],
            [new("LockedQueryModel", [new("Secret", "string", true, false) {IsSensitive = true}])],
            []);

        var index = new SchemaIndex(introspection);

        await Assert.That(index.StarterQuery(index.Sources[0])).IsEqualTo("Query.Locked");
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
