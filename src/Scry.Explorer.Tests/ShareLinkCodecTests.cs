// The share link is a compatibility contract: links already in circulation have to keep opening, so
// these pin the spelling as well as the round trip.
public class ShareLinkCodecTests
{
    [Test]
    public async Task RoundTripsAQuery()
    {
        var code = "Query.Employee\n    .Where(_ => _.Active)\n    .Select(_ => new { _.Name })";

        await Assert.That(ShareLinkCodec.Decode(ShareLinkCodec.Encode(code))).IsEqualTo(code);
    }

    // base64url: '+' and '/' are replaced and the padding dropped, so the fragment survives a URL
    // unchanged.
    [Test]
    public async Task EncodesAsUnpaddedBase64Url()
    {
        var encoded = ShareLinkCodec.Encode("Query.Employee.Where(_ => _.Active)");

        await Assert.That(encoded).StartsWith("#q=");

        // The prefix carries an '=' of its own, so the padding assertion is of the payload.
        var payload = encoded["#q=".Length..];
        await Assert.That(payload).DoesNotContain("+");
        await Assert.That(payload).DoesNotContain("/");
        await Assert.That(payload).DoesNotContain("=");
    }

    [Test]
    public async Task RoundTripsNonAscii()
    {
        var code = "Query.Employee.Where(_ => _.Name == \"Ünïcödé ☃\")";

        await Assert.That(ShareLinkCodec.Decode(ShareLinkCodec.Encode(code))).IsEqualTo(code);
    }

    // A shared link is untrusted input, so anything that does not decode is ignored rather than
    // surfaced — the explorer opens on its sample query instead of on an error.
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("#")]
    [Arguments("#other=1")]
    [Arguments("#q=")]
    [Arguments("#q=not!base64")]
    public async Task IgnoresAFragmentThatDoesNotDecode(string? hash) =>
        await Assert.That(ShareLinkCodec.Decode(hash)).IsNull();

    // The fragment arrives percent-encoded when the browser has escaped it.
    [Test]
    public async Task DecodesAPercentEncodedFragment()
    {
        var encoded = ShareLinkCodec.Encode("Query.Employee");
        var escaped = "#q=" + Uri.EscapeDataString(encoded["#q=".Length..]);

        await Assert.That(ShareLinkCodec.Decode(escaped)).IsEqualTo("Query.Employee");
    }
}
