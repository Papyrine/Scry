/// <summary>
/// The two spellings a projection member has on the wire: one reading the member it is named for
/// travels as a bare string, everything else as an object. <c>WireShapeTests</c> pins what each
/// spelling looks like across the operator set; this pins that a member has only one of them, which is
/// what lets a request be keyed on its bytes.
/// </summary>
[TestFixture]
public class ProjectionMemberEncodingTests
{
    [Test]
    public void AMemberReadingItsOwnNameTravelsAsAString() =>
        Assert.That(
            Serialize(new("Active", new NodeValue(new MemberNode(["Active"])))),
            Does.Contain("\"members\":[\"Active\"]"));

    [Test]
    public void AStringReadsBackAsTheMemberItStandsFor()
    {
        var json = Serialize(new("Active", new NodeValue(new MemberNode(["Active"]))));
        var member = Deserialize("[\"Active\"]").Members.Single();

        Assert.Multiple(() =>
        {
            Assert.That(member.Name, Is.EqualTo("Active"));
            Assert.That(
                member.Value is NodeValue {Node: MemberNode {Path: ["Active"]}},
                "the string stands for a member reading the path it names");
            // And the trip closes: what the short form reads back to writes back out as the short form.
            Assert.That(ScryJson.Serialize(ScryJson.DeserializeRequest(json)), Is.EqualTo(json));
        });
    }

    // The three shapes that keep the object form: a member renamed away from what it reads, one
    // reaching through a navigation, and one that is not a member read at all.
    [TestCaseSource(nameof(ObjectFormCases))]
    public void AMemberThatDoesNotReadItsOwnNameTravelsAsAnObject(ProjectionMember member) =>
        Assert.That(Serialize(member), Does.Contain("\"name\":"));

    static IEnumerable<ProjectionMember> ObjectFormCases()
    {
        yield return new("IsActive", new NodeValue(new MemberNode(["Active"])));
        yield return new("Name", new NodeValue(new MemberNode(["Manager", "Name"])));
        yield return new("Upper", new NodeValue(new CallNode(KnownFunction.StringToUpper, new MemberNode(["Name"]), [])));
    }

    // The canonicity guard. Both spellings deserializing would make two requests meaning the same thing
    // two different sets of bytes, which is what the ETag and the request fingerprint key off.
    [Test]
    public void TheObjectSpellingOfAMemberReadingItsOwnNameIsRefused()
    {
        var exception = Assert.Throws<ScryWireException>(
            () => Deserialize("""[{"name":"Active","value":{"$type":"node","node":{"$type":"member","path":"Active"}}}]"""))!;

        Assert.That(exception.Message, Does.Contain("""is written as a string: "Active"."""));
    }

    // The same member one level down, so the refusal is known to reach through a nested projection
    // rather than only applying to the members the pipeline names directly.
    [Test]
    public void TheObjectSpellingIsRefusedInsideANestedProjection() =>
        Assert.Throws<ScryWireException>(
            () => Deserialize(
                """
                [{"name":"Department","value":{"$type":"nested","path":"Department","projection":{"members":[{"name":"Name","value":{"$type":"node","node":{"$type":"member","path":"Name"}}}]}}}]
                """));

    [Test]
    public void ABlankMemberNameIsRefused() =>
        Assert.Throws<ScryWireException>(() => Deserialize("[\" \"]"));

    [Test]
    public void AMemberMissingItsValueIsRefused() =>
        Assert.Throws<ScryWireException>(() => Deserialize("""[{"name":"Active"}]"""));

    [Test]
    public void AMemberThatIsNeitherAStringNorAnObjectIsRefused() =>
        Assert.Throws<ScryWireException>(() => Deserialize("[7]"));

    static string Serialize(ProjectionMember member) =>
        ScryJson.Serialize(QueryRequest.Create("Employee", [new SelectOp(new([member]))]));

    static Projection Deserialize(string members) =>
        ScryJson.DeserializeRequest(
                $$$"""
                   {"version":1,"root":"Employee","pipeline":[{"$type":"select","projection":{"members":{{{members}}}}}]}
                   """)
            .Pipeline
            .OfType<SelectOp>()
            .Single()
            .Projection;
}
