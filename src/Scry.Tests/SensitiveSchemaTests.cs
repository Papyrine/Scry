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
}
