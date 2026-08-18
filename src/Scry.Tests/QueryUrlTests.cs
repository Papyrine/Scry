using Microsoft.AspNetCore.WebUtilities;
using NaughtyStrings;

/// <summary>
/// The two encodings a request can take in a URL: that each round-trips, that a server tells them apart
/// without either announcing itself, and that the default one survives a real query-string parser
/// whatever a constant happens to contain.
/// </summary>
/// <remarks>
/// The last of those is the risk percent-encoded JSON carries and base64url does not. JSON puts
/// attacker-supplied text into a query string, so an <c>&amp;</c> or an <c>=</c> that reached the URL
/// unescaped would end the parameter early and the request would decode into something other than what
/// was asked — which is why these go through <see cref="QueryHelpers.ParseQuery"/> rather than through
/// <see cref="Uri.UnescapeDataString"/>, the inverse of the call being tested and therefore no evidence
/// at all.
/// </remarks>
[TestFixture]
public class QueryUrlTests
{
    static QueryRequest Request(string constant = "Alice") =>
        QueryRequest.Create(
            "Employees",
            [
                new WhereOp(
                    new BinaryNode(
                        BinaryOp.Equal,
                        new MemberNode(["Name"]),
                        new ConstNode(constant, ClrTypeTag.String)))
            ]);

    // A record holding a list compares by reference, so what is compared is the serialization — the
    // bytes the request would have travelled as either way.
    static string Json(QueryRequest request) =>
        Encoding.UTF8.GetString(ScryJson.SerializeToUtf8(request));

    // What reaches a server: the value already pulled out of a query string and percent-decoded.
    static QueryRequest Parse(string encoded) =>
        QueryUrl.Decode(QueryHelpers.ParseQuery($"?{QueryUrl.Parameter}={encoded}")[QueryUrl.Parameter]!);

    [Test]
    public void JsonIsTheDefault()
    {
        var request = Request();
        var encoded = QueryUrl.Encode(request);

        Assert.Multiple(() =>
        {
            // Escaped on the way out, so the parameter is appended to a URL as it stands...
            Assert.That(encoded, Does.Not.Contain("{"));

            // ...and is the request itself once a query-string parser has undone that.
            Assert.That(Json(Parse(encoded)), Is.EqualTo(Json(request)));
        });
    }

    [Test]
    public void Base64UrlIsOptIn()
    {
        var request = Request();
        var encoded = QueryUrl.Encode(request, QueryUrlEncoding.Base64Url);

        Assert.Multiple(() =>
        {
            Assert.That(
                encoded.All(_ => char.IsAsciiLetterOrDigit(_) || _ is '-' or '_'),
                Is.True,
                $"not url-safe: {encoded}");
            Assert.That(Json(Parse(encoded)), Is.EqualTo(Json(request)));
        });
    }

    // Neither encoding says which it is, and neither has to: a server reads the first character.
    [Test]
    public void EitherEncodingIsAcceptedOnTheSameParameter()
    {
        var request = Request();

        Assert.That(
            Json(Parse(QueryUrl.Encode(request))),
            Is.EqualTo(Json(Parse(QueryUrl.Encode(request, QueryUrlEncoding.Base64Url)))));
    }

    // The trade the default makes, pinned rather than described: the same query is markedly longer as
    // percent-encoded JSON, so it reaches QueryUrl.MaxLength — and falls back to a body — sooner.
    [Test]
    public void JsonCostsLengthThatBase64UrlDoesNot()
    {
        var request = Request();

        Assert.That(
            QueryUrl.Encode(request).Length,
            Is.GreaterThan(QueryUrl.Encode(request, QueryUrlEncoding.Base64Url).Length));
    }

    // The characters that would end the parameter early, or be read as something else, if the default
    // encoding escaped anything less than it does.
    [TestCase("a&b=c")]
    [TestCase("a+b")]
    [TestCase("a#b")]
    [TestCase("100%")]
    [TestCase("a/b?c")]
    [TestCase("\"quoted\"")]
    [TestCase("{\"nested\":\"json\"}")]
    public void QueryStringSyntaxInAConstantSurvives(string constant)
    {
        var request = Request(constant);

        Assert.That(Json(Parse(QueryUrl.Encode(request))), Is.EqualTo(Json(request)));
    }

    // The same claim, made against strings picked to break exactly this kind of code.
    [Test]
    public void NaughtyConstantsSurvive()
    {
        foreach (var naughty in TheNaughtyStrings.All)
        {
            if (naughty.Length == 0)
            {
                continue;
            }

            var request = Request(naughty);

            Assert.That(
                Json(Parse(QueryUrl.Encode(request))),
                Is.EqualTo(Json(request)),
                $"did not survive: {naughty}");
        }
    }

    [Test]
    public void MissingParameterIsRefused() =>
        Assert.Throws<ScryWireException>(() => QueryUrl.Decode(""));

    // Not JSON, so it is read as base64url — and it is not that either.
    [Test]
    public void MalformedParameterIsRefused() =>
        Assert.Throws<ScryWireException>(() => QueryUrl.Decode("not-base64url!!"));
}
