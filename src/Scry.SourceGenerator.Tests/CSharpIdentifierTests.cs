/// <summary>
/// The identifier rules are hand-rolled rather than taken from Roslyn, because the server compiles
/// the same source and cannot reference it. That makes Roslyn the authority these have to be pinned
/// against: anything accepted here is emitted raw into generated C#, so accepting something the
/// compiler would reject is the failure that matters.
/// </summary>
public class CSharpIdentifierTests
{
    [Test]
    [Arguments("Employee")]
    [Arguments("_Employee")]
    [Arguments("Employee2")]
    [Arguments("_")]
    [Arguments("Ärger")]
    [Arguments("Ünïcödé")]
    public async Task Accepts(string name) =>
        await Assert.That(CSharpIdentifier.IsValid(name)).IsTrue();

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("Sales Region")]
    [Arguments("2Fast")]
    [Arguments("Sales-Region")]
    [Arguments("Sales.Region")]
    [Arguments("a\"b")]
    [Arguments("Region;DropTable")]
    // A verbatim prefix is not accepted: the same string is the wire name, which carries no '@'.
    [Arguments("@class")]
    public async Task Rejects(string? name) =>
        await Assert.That(CSharpIdentifier.IsValid(name)).IsFalse();

    // A reserved keyword needs an '@' to be written as a member name, so it cannot be a source name.
    // Pinned against Roslyn's own list rather than a second hand-written one, so a keyword missing
    // from the shared set fails here instead of in a consumer's generated code.
    [Test]
    public async Task RejectsEveryReservedKeyword()
    {
        var keywords = SyntaxFacts.GetReservedKeywordKinds().Select(SyntaxFacts.GetText).ToList();

        await Assert.That(keywords).IsNotEmpty();
        foreach (var keyword in keywords)
        {
            await Assert.That(CSharpIdentifier.IsValid(keyword)).IsFalse().Because(keyword);
        }
    }

    // The other half of the same pin: a contextual keyword is a legal member name, so refusing one
    // would reject a source name C# is perfectly happy to express.
    [Test]
    public async Task AcceptsEveryContextualKeyword()
    {
        var keywords = SyntaxFacts.GetContextualKeywordKinds().Select(SyntaxFacts.GetText).ToList();

        await Assert.That(keywords).IsNotEmpty();
        foreach (var keyword in keywords)
        {
            await Assert.That(CSharpIdentifier.IsValid(keyword)).IsTrue().Because(keyword);
        }
    }

    // Erring strict is safe — a rejected name is reported against the model — but erring loose emits
    // code that does not parse, so nothing accepted here may be something Roslyn would refuse.
    [Test]
    [Arguments("Employee")]
    [Arguments("_")]
    [Arguments("Ärger")]
    [Arguments("Sales Region")]
    [Arguments("2Fast")]
    [Arguments("@class")]
    [Arguments("class")]
    [Arguments("var")]
    public async Task AcceptsNothingRoslynRejects(string name)
    {
        if (!CSharpIdentifier.IsValid(name))
        {
            return;
        }

        await Assert.That(SyntaxFacts.IsValidIdentifier(name)).IsTrue().Because(name);
        await Assert.That(SyntaxFacts.GetKeywordKind(name)).IsEqualTo(SyntaxKind.None).Because(name);
    }
}
