/// <summary>
/// The server's answer to the walk's question, asked directly. Two of the shapes are the walk's own
/// blunt ones: a path with no source behind it is answered from the names every allow-listed type
/// marks, and that table is built with the schema rather than by the first request to need it.
/// </summary>
[TestFixture]
public class SensitiveSchemaTests
{
    static readonly SensitiveSchema sensitive = Build();

    static SensitiveSchema Build()
    {
        var options = new ScryOptions(typeof(TestContext));
        options.AddPocoSource<Holiday>(_ => Holiday.Seed());
        return new(Schema.Build(options));
    }

    [Test]
    public void AMarkedMemberIsReachedThroughAnOptionalStruct() =>
        Assert.Multiple(() =>
        {
            Assert.That(sensitive.IsSensitive("Employee", ["Workstation", "Extension"]), Is.True);
            Assert.That(sensitive.IsSensitive("Employee", ["Workstation", "Room"]), Is.False);
            Assert.That(sensitive.IsSensitive("Employee", ["Workstation"]), Is.False);
        });

    [Test]
    public void AMarkedTypeMarksEveryPathInto() =>
        Assert.Multiple(() =>
        {
            Assert.That(sensitive.IsSensitive("Employee", ["Address"]), Is.True);
            Assert.That(sensitive.IsSensitive("Employee", ["Address", "City"]), Is.True);
            Assert.That(sensitive.IsSensitive("Employee", ["PreviousAddresses"]), Is.True);
        });

    // A source returned whole returns its marked members with it.
    [Test]
    public void AnEmptyPathAsksAboutTheSource() =>
        Assert.Multiple(() =>
        {
            Assert.That(sensitive.IsSensitive("Employee", []), Is.True);
            Assert.That(sensitive.IsSensitive("Department", []), Is.False);
        });

    // With no source to read off — after a flatten, a group, a join — any segment naming a member some
    // type marks answers yes, and one naming nothing marked answers no, whichever type it is really on.
    [Test]
    public void AnUnresolvedPathIsAnsweredByName() =>
        Assert.Multiple(() =>
        {
            Assert.That(sensitive.IsSensitive(null, ["Extension"]), Is.True);
            Assert.That(sensitive.IsSensitive(null, ["Region", "Avatar"]), Is.True);
            Assert.That(sensitive.IsSensitive(null, ["Room"]), Is.False);
            Assert.That(sensitive.IsSensitive("NoSuchSource", ["Extension"]), Is.True);
            Assert.That(sensitive.IsSensitive("Employee", ["NoSuchMember", "Extension"]), Is.True);
        });
}
