// The share link is a compatibility contract: links already in circulation have to keep opening, so
// these pin the spelling as well as the round trip.
[TestFixture]
public class ShareLinkCodecTests
{
    [Test]
    public void RoundTripsAQuery()
    {
        var code = "Query.Employee\n    .Where(_ => _.Active)\n    .Select(_ => new { _.Name })";

        Assert.That(ShareLinkCodec.Decode(ShareLinkCodec.Encode(code)), Is.EqualTo(code));
    }

    // base64url: '+' and '/' are replaced and the padding dropped, so the fragment survives a URL
    // unchanged.
    [Test]
    public void EncodesAsUnpaddedBase64Url()
    {
        var encoded = ShareLinkCodec.Encode("Query.Employee.Where(_ => _.Active)");

        Assert.That(encoded, Does.StartWith("#q="));

        // The prefix carries an '=' of its own, so the padding assertion is of the payload.
        var payload = encoded["#q=".Length..];
        Assert.That(payload, Does.Not.Contain("+"));
        Assert.That(payload, Does.Not.Contain("/"));
        Assert.That(payload, Does.Not.Contain("="));
    }

    [Test]
    public void RoundTripsNonAscii()
    {
        var code = "Query.Employee.Where(_ => _.Name == \"Ünïcödé ☃\")";

        Assert.That(ShareLinkCodec.Decode(ShareLinkCodec.Encode(code)), Is.EqualTo(code));
    }

    // A shared link is untrusted input, so anything that does not decode is ignored rather than
    // surfaced — the explorer opens on its sample query instead of on an error.
    [TestCase(null)]
    [TestCase("")]
    [TestCase("#")]
    [TestCase("#other=1")]
    [TestCase("#q=")]
    [TestCase("#q=not!base64")]
    public void IgnoresAFragmentThatDoesNotDecode(string? hash) =>
        Assert.That(ShareLinkCodec.Decode(hash), Is.Null);

    // The fragment arrives percent-encoded when the browser has escaped it.
    [Test]
    public void DecodesAPercentEncodedFragment()
    {
        var encoded = ShareLinkCodec.Encode("Query.Employee");
        var escaped = "#q=" + Uri.EscapeDataString(encoded["#q=".Length..]);

        Assert.That(ShareLinkCodec.Decode(escaped), Is.EqualTo("Query.Employee"));
    }
}
