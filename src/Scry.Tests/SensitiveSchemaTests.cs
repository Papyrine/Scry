/// <summary>
/// The server's answer to the walk's question, asked directly. Two of the shapes are the walk's own
/// blunt ones: a path with no source behind it is answered from the names every allow-listed type
/// marks, and that table is built with the schema rather than by the first request to need it.
/// </summary>
public class SensitiveSchemaTests
{
    static SensitiveSchema sensitive = Build();

    static SensitiveSchema Build()
    {
        var options = new ScryOptions(typeof(TestContext));
        options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        return new(Schema.Build(options));
    }

    [Test]
    public async Task AMarkedMemberIsReachedThroughAnOptionalStruct()
    {
        using (Assert.Multiple())
        {
            await Assert.That(sensitive.IsSensitive("Employee", ["Workstation", "Extension"])).IsTrue();
            await Assert.That(sensitive.IsSensitive("Employee", ["Workstation", "Room"])).IsFalse();
            await Assert.That(sensitive.IsSensitive("Employee", ["Workstation"])).IsFalse();
        }
    }

    [Test]
    public async Task AMarkedTypeMarksEveryPathInto()
    {
        using (Assert.Multiple())
        {
            await Assert.That(sensitive.IsSensitive("Employee", ["Address"])).IsTrue();
            await Assert.That(sensitive.IsSensitive("Employee", ["Address", "City"])).IsTrue();
            await Assert.That(sensitive.IsSensitive("Employee", ["PreviousAddresses"])).IsTrue();
        }
    }

    // Marked on a base and overridden without the attribute: the generator carries the base's marking
    // onto the override, and the server has to agree, or the member is sensitive to the client and
    // not to the server.
    [Test]
    public async Task AMarkedBasePropertyIsMarkedThroughItsOverride()
    {
        using (Assert.Multiple())
        {
            await Assert.That(sensitive.IsSensitive("Invoice", ["Reviewer"])).IsTrue();
            await Assert.That(sensitive.IsSensitive("Invoice", ["Notes"])).IsFalse();
        }
    }

    // A source returned whole returns its marked members with it.
    [Test]
    public async Task AnEmptyPathAsksAboutTheSource()
    {
        using (Assert.Multiple())
        {
            await Assert.That(sensitive.IsSensitive("Employee", [])).IsTrue();
            await Assert.That(sensitive.IsSensitive("Department", [])).IsFalse();
        }
    }

    // With no source to read off — after a flatten, a group, a join — any segment naming a member some
    // type marks answers yes, and one naming nothing marked answers no, whichever type it is really on.
    [Test]
    public async Task AnUnresolvedPathIsAnsweredByName()
    {
        using (Assert.Multiple())
        {
            await Assert.That(sensitive.IsSensitive(null, ["Extension"])).IsTrue();
            await Assert.That(sensitive.IsSensitive(null, ["Region", "Avatar"])).IsTrue();
            await Assert.That(sensitive.IsSensitive(null, ["Room"])).IsFalse();
            await Assert.That(sensitive.IsSensitive("NoSuchSource", ["Extension"])).IsTrue();
            await Assert.That(sensitive.IsSensitive("Employee", ["NoSuchMember", "Extension"])).IsTrue();
        }
    }

    // The segment the walk ends a path with to ask about the type the path reaches rather than a
    // member of it — the element of a collection a query flattened into and returned whole. The same
    // question an empty path asks of a source, one collection further along.
    [Test]
    public async Task ThePathToACollectionsElementAsksAboutTheElement()
    {
        using (Assert.Multiple())
        {
            await Assert.That(sensitive.IsSensitive("Employee", [anyMember])).IsTrue();
            await Assert.That(sensitive.IsSensitive("Department", [anyMember])).IsFalse();
            await Assert.That(sensitive.IsSensitive("Order", ["Lines", anyMember])).IsFalse();
            await Assert.That(sensitive.IsSensitive("Employee", ["PreviousAddresses", anyMember])).IsTrue();
        }
    }

    // Off a row this cannot name, a row returned whole holds every member of some type — so if any type
    // marks one, it is taken to be among them.
    [Test]
    public async Task AnUnresolvedRowReturnedWholeIsMarkedWhereAnythingIs() =>
        await Assert.That(sensitive.IsSensitive(null, ["Entries", anyMember])).IsTrue();

    // SensitiveWalk.AnyMember, which is compiled into the client and the server both and so cannot be
    // named from here without an alias. A change to it fails these loudly.
    const string anyMember = "*";
}
