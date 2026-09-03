/// <summary>
/// The paging cursor's encoding. The codec now uses the framework's base64url rather than
/// substituting characters into a base64 string by hand, so what these pin is the alphabet it
/// produces, that a cursor survives the round trip whatever the values, and that a tampered or
/// malformed one is still refused rather than half-read.
/// </summary>
[TestFixture]
public class CursorCodecTests
{
    static readonly byte[] key = Enumerable.Range(0, 32).Select(_ => (byte) _).ToArray();

    const string order = "abcdefghijkl";

    [Test]
    public void RoundTripsValuesAndTags()
    {
        var cursor = CursorCodec.Encode(
            [("Alice", ClrTypeTag.String), ("42", ClrTypeTag.Int32), (null, ClrTypeTag.Null)],
            order,
            key);

        var (values, decoded) = CursorCodec.Decode(cursor, key);

        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.EqualTo(order));
            Assert.That(values.Select(_ => _.Value), Is.EqualTo(["Alice", "42", null]));
            Assert.That(values.Select(_ => _.Tag), Is.EqualTo([ClrTypeTag.String, ClrTypeTag.Int32, ClrTypeTag.Null]));
        });
    }

    // An ordering key is spelled the way the client spells the same value as a constant, and for the
    // same reason: it is parsed back against the member's own type. The default text of a time of day
    // stops at the minute and that of an offset at the second, so a key encoded through either would
    // seek from a boundary the page did not end on — the rows between the two are then repeated or
    // skipped, silently, with the cursor's signature still valid.
    [TestCase("05:06:07.1230000")]
    public void ATimeOfDayKeyKeepsItsSeconds(string expected) =>
        Assert.That(CursorCodec.TagValue(new Time(5, 6, 7, 123)).Value, Is.EqualTo(expected));

    [TestCase("2026-03-04T05:06:07.1230000+02:00")]
    public void AnOffsetKeyKeepsItsSubSecondPart(string expected) =>
        Assert.That(
            CursorCodec.TagValue(new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.FromHours(2))).Value,
            Is.EqualTo(expected));

    // Two servers of one deployment can sit in different zones, so the encoding side's offset is not
    // something the decoding side can read. The wall clock the provider binds is carried instead.
    [TestCase("2026-09-03T00:00:00.0000000")]
    public void ALocalTimestampKeyCarriesNoOffset(string expected) =>
        Assert.That(
            CursorCodec.TagValue(new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Local)).Value,
            Is.EqualTo(expected));

    // Base64url, so a cursor is safe in a query string or a path segment without further escaping.
    [Test]
    public void ProducesOnlyUrlSafeCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            var cursor = CursorCodec.Encode(
                [($"value{i}~?{(char) ('a' + i % 26)}", ClrTypeTag.String)],
                order,
                key);

            Assert.That(
                cursor.All(_ => char.IsAsciiLetterOrDigit(_) || _ is '-' or '_' or '.'),
                Is.True,
                $"'{cursor}' carries a character that is not base64url");
        }
    }

    // Base64 emits its 62nd and 63rd characters, the two the url alphabet substitutes, for the sextets
    // 111110 and 111111. The only way a payload of unescaped ASCII reaches those is a byte of 0x7E
    // ('~') or 0x3F ('?') landing last in a three-byte group, so a run of each covers every alignment
    // and drives both substitutions rather than hoping a random payload happens to.
    [Test]
    public void RoundTripsValuesWhoseEncodingUsesTheSubstitutedCharacters()
    {
        var text = new string('~', 12) + new string('?', 12);
        var cursor = CursorCodec.Encode([(text, ClrTypeTag.String)], order, key);

        Assert.Multiple(() =>
        {
            Assert.That(cursor, Does.Contain("-"));
            Assert.That(cursor, Does.Contain("_"));
            Assert.That(cursor, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
            Assert.That(CursorCodec.Decode(cursor, key).Values.Single().Value, Is.EqualTo(text));
        });
    }

    [Test]
    public void RefusesACursorSignedWithAnotherKey()
    {
        var cursor = CursorCodec.Encode([("Alice", ClrTypeTag.String)], order, key);
        var other = Enumerable.Repeat((byte) 9, 32).ToArray();

        Assert.Throws<ScryValidationException>(() => CursorCodec.Decode(cursor, other));
    }

    [Test]
    public void RefusesATamperedPayload()
    {
        var cursor = CursorCodec.Encode([("Alice", ClrTypeTag.String)], order, key);
        var dot = cursor.IndexOf('.');
        var flipped = cursor[1] == 'A' ? 'B' : 'A';
        var tampered = $"{cursor[0]}{flipped}{cursor[2..dot]}{cursor[dot..]}";

        Assert.Throws<ScryValidationException>(() => CursorCodec.Decode(tampered, key));
    }

    [TestCase("")]
    [TestCase(".")]
    [TestCase("nodot")]
    [TestCase("trailing.")]
    [TestCase(".leading")]
    [TestCase("not!base64url.alsonot!")]
    [TestCase("YWJj.YWJj")]
    public void RefusesAMalformedCursor(string cursor) =>
        Assert.Throws<ScryValidationException>(() => CursorCodec.Decode(cursor, key));

    [Test]
    public void StampsAnOrderingByItsKeysAndDirections()
    {
        var byName = CursorCodec.OrderStamp("Employee", [(new MemberNode(["Name"]), false)]);

        Assert.Multiple(() =>
        {
            Assert.That(CursorCodec.OrderStamp("Employee", [(new MemberNode(["Name"]), false)]), Is.EqualTo(byName));
            // Every way a cursor could be applied to an ordering it was not issued for stamps apart.
            Assert.That(CursorCodec.OrderStamp("Employee", [(new MemberNode(["Name"]), true)]), Is.Not.EqualTo(byName));
            Assert.That(CursorCodec.OrderStamp("Employee", [(new MemberNode(["Id"]), false)]), Is.Not.EqualTo(byName));
            Assert.That(CursorCodec.OrderStamp("Order", [(new MemberNode(["Name"]), false)]), Is.Not.EqualTo(byName));
            Assert.That(
                CursorCodec.OrderStamp("Employee", [(new MemberNode(["Name"]), false), (new MemberNode(["Id"]), false)]),
                Is.Not.EqualTo(byName));
        });
    }

    // The canonical form is encoded into a stack buffer, with a rented one past its size, so an
    // ordering long enough to need the second must stamp as stably as a short one.
    [Test]
    public void StampsAnOrderingTooLongForTheStackBuffer()
    {
        var keys = Enumerable.Range(0, 100)
            .Select(_ => (Key: (Node) new MemberNode([new string('m', 40) + _]), Descending: _ % 2 == 0))
            .ToArray();

        var stamp = CursorCodec.OrderStamp("Employee", keys);

        Assert.Multiple(() =>
        {
            Assert.That(stamp, Has.Length.EqualTo(16));
            Assert.That(CursorCodec.OrderStamp("Employee", keys), Is.EqualTo(stamp));
        });
    }
}
