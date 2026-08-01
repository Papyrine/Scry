/// <summary>
/// The identifier rules are hand-rolled rather than taken from Roslyn, because the server compiles
/// the same source and cannot reference it. That makes Roslyn the authority these have to be pinned
/// against: anything accepted here is emitted raw into generated C#, so accepting something the
/// compiler would reject is the failure that matters.
/// </summary>
[TestFixture]
public class CSharpIdentifierTests
{
    [TestCase("Employee")]
    [TestCase("_Employee")]
    [TestCase("Employee2")]
    [TestCase("_")]
    [TestCase("Ärger")]
    [TestCase("Ünïcödé")]
    public void Accepts(string name) =>
        Assert.That(CSharpIdentifier.IsValid(name), Is.True);

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("Sales Region")]
    [TestCase("2Fast")]
    [TestCase("Sales-Region")]
    [TestCase("Sales.Region")]
    [TestCase("a\"b")]
    [TestCase("Region;DropTable")]
    // A verbatim prefix is not accepted: the same string is the wire name, which carries no '@'.
    [TestCase("@class")]
    public void Rejects(string? name) =>
        Assert.That(CSharpIdentifier.IsValid(name), Is.False);

    // A reserved keyword needs an '@' to be written as a member name, so it cannot be a source name.
    // Pinned against Roslyn's own list rather than a second hand-written one, so a keyword missing
    // from the shared set fails here instead of in a consumer's generated code.
    [Test]
    public void RejectsEveryReservedKeyword()
    {
        var keywords = SyntaxFacts.GetReservedKeywordKinds().Select(SyntaxFacts.GetText).ToList();

        Assert.That(keywords, Is.Not.Empty);
        foreach (var keyword in keywords)
        {
            Assert.That(CSharpIdentifier.IsValid(keyword), Is.False, keyword);
        }
    }

    // The other half of the same pin: a contextual keyword is a legal member name, so refusing one
    // would reject a source name C# is perfectly happy to express.
    [Test]
    public void AcceptsEveryContextualKeyword()
    {
        var keywords = SyntaxFacts.GetContextualKeywordKinds().Select(SyntaxFacts.GetText).ToList();

        Assert.That(keywords, Is.Not.Empty);
        foreach (var keyword in keywords)
        {
            Assert.That(CSharpIdentifier.IsValid(keyword), Is.True, keyword);
        }
    }

    // Erring strict is safe — a rejected name is reported against the model — but erring loose emits
    // code that does not parse, so nothing accepted here may be something Roslyn would refuse.
    [TestCase("Employee")]
    [TestCase("_")]
    [TestCase("Ärger")]
    [TestCase("Sales Region")]
    [TestCase("2Fast")]
    [TestCase("@class")]
    [TestCase("class")]
    [TestCase("var")]
    public void AcceptsNothingRoslynRejects(string name)
    {
        if (!CSharpIdentifier.IsValid(name))
        {
            return;
        }

        Assert.That(SyntaxFacts.IsValidIdentifier(name), Is.True, name);
        Assert.That(SyntaxFacts.GetKeywordKind(name), Is.EqualTo(SyntaxKind.None), name);
    }
}
