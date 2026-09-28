/// <summary>
/// The paging cursor's encoding: base64url of an AES-GCM sealed payload. What these pin is the
/// alphabet it produces, that a cursor survives the round trip whatever the values, that nothing
/// inside it is readable without the key, and that a tampered or malformed one is refused rather
/// than half-read.
/// </summary>
public class CursorCodecTests
{
    static byte[] key = Enumerable.Range(0, 32).Select(_ => (byte) _).ToArray();

    const string order = "abcdefghijkl";

    [Test]
    public async Task RoundTripsValuesAndTags()
    {
        var cursor = CursorCodec.Encode(
            [("Alice", ClrTypeTag.String), ("42", ClrTypeTag.Int32), (null, ClrTypeTag.Null)],
            order,
            key);

        var (values, decoded) = CursorCodec.Decode(cursor, key);

        using (Assert.Multiple())
        {
            await Assert.That(decoded).IsEqualTo(order);
            await Assert.That(values.Select(_ => _.Value)).IsEquivalentTo(["Alice", "42", null], CollectionOrdering.Matching);
            await Assert.That(values.Select(_ => _.Tag)).IsEquivalentTo([ClrTypeTag.String, ClrTypeTag.Int32, ClrTypeTag.Null], CollectionOrdering.Matching);
        }
    }

    // An ordering key is spelled the way the client spells the same value as a constant, and for the
    // same reason: it is parsed back against the member's own type. The default text of a time of day
    // stops at the minute and that of an offset at the second, so a key encoded through either would
    // seek from a boundary the page did not end on — the rows between the two are then repeated or
    // skipped, silently, with the cursor's signature still valid.
    [Test]
    [Arguments("05:06:07.1230000")]
    public async Task ATimeOfDayKeyKeepsItsSeconds(string expected) =>
        await Assert.That(CursorCodec.TagValue(new Time(5, 6, 7, 123)).Value).IsEqualTo(expected);

    [Test]
    [Arguments("2026-03-04T05:06:07.1230000+02:00")]
    public async Task AnOffsetKeyKeepsItsSubSecondPart(string expected) =>
        await Assert.That(CursorCodec.TagValue(new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.FromHours(2))).Value).IsEqualTo(expected);

    // Two servers of one deployment can sit in different zones, so the encoding side's offset is not
    // something the decoding side can read. The wall clock the provider binds is carried instead.
    [Test]
    [Arguments("2026-09-03T00:00:00.0000000")]
    public async Task ALocalTimestampKeyCarriesNoOffset(string expected) =>
        await Assert.That(CursorCodec.TagValue(new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Local)).Value).IsEqualTo(expected);

    // Base64url, so a cursor is safe in a query string or a path segment without further escaping.
    [Test]
    public async Task ProducesOnlyUrlSafeCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            var cursor = CursorCodec.Encode(
                [($"value{i}~?{(char) ('a' + i % 26)}", ClrTypeTag.String)],
                order,
                key);

            await Assert.That(cursor.All(_ => char.IsAsciiLetterOrDigit(_) || _ is '-' or '_' or '.')).IsTrue().Because($"'{cursor}' carries a character that is not base64url");
        }
    }

    // The token is ciphertext, so its alphabet is the encoding's alone: base64url, with nothing to
    // escape and no padding, whatever the values inside it.
    [Test]
    public async Task RoundTripsValuesWhoseEncodingUsesTheSubstitutedCharacters()
    {
        var text = new string('~', 12) + new string('?', 12);
        var cursor = CursorCodec.Encode([(text, ClrTypeTag.String)], order, key);

        using (Assert.Multiple())
        {
            await Assert.That(cursor).DoesNotContain("+").And.DoesNotContain("/").And.DoesNotContain("=");
            await Assert.That(CursorCodec.Decode(cursor, key).Values.Single().Value).IsEqualTo(text);
        }
    }

    // Sealed rather than signed: the ordering key's value can be a [Sensitive] member's, and a cursor
    // travels in the URL of the next page — into every access log the marking exists to keep it out of.
    [Test]
    public async Task DoesNotCarryTheKeyValuesInTheClear()
    {
        var cursor = CursorCodec.Encode([("Alice", ClrTypeTag.String)], order, key);
        var bytes = Base64Url.DecodeFromChars(cursor);

        using (Assert.Multiple())
        {
            await Assert.That(Encoding.Latin1.GetString(bytes)).DoesNotContain("Alice");
            await Assert.That(Encoding.Latin1.GetString(bytes)).DoesNotContain(order);
        }
    }

    // A fresh nonce per cursor: two cursors for one row never share bytes, so a token cannot be
    // matched against another to learn that two pages ended on the same row.
    [Test]
    public async Task SealsTheSameValuesDifferentlyEachTime()
    {
        var first = CursorCodec.Encode([("Alice", ClrTypeTag.String)], order, key);
        var second = CursorCodec.Encode([("Alice", ClrTypeTag.String)], order, key);

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public void RefusesACursorSignedWithAnotherKey()
    {
        var cursor = CursorCodec.Encode([("Alice", ClrTypeTag.String)], order, key);
        var other = Enumerable.Repeat((byte) 9, 32).ToArray();

        Assert.ThrowsExactly<ScryValidationException>(() => CursorCodec.Decode(cursor, other));
    }

    [Test]
    public void RefusesATamperedPayload()
    {
        var cursor = CursorCodec.Encode([("Alice", ClrTypeTag.String)], order, key);
        // A character past the nonce, inside the ciphertext, so the tag no longer stands for it.
        var index = 20;
        var flipped = cursor[index] == 'A' ? 'B' : 'A';
        var tampered = $"{cursor[..index]}{flipped}{cursor[(index + 1)..]}";

        Assert.ThrowsExactly<ScryValidationException>(() => CursorCodec.Decode(tampered, key));
    }

    [Test]
    [Arguments("")]
    [Arguments("not!base64url")]
    [Arguments("YWJj")]
    [Arguments("eyJrIjpbXX0.c2ln")]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void RefusesAMalformedCursor(string cursor) =>
        Assert.ThrowsExactly<ScryValidationException>(() => CursorCodec.Decode(cursor, key));

    [Test]
    public async Task StampsAnOrderingByItsKeysAndDirections()
    {
        var byName = CursorCodec.OrderStamp("Employee", [], [(new MemberNode(["Name"]), false)]);

        using (Assert.Multiple())
        {
            await Assert.That(CursorCodec.OrderStamp("Employee", [], [(new MemberNode(["Name"]), false)])).IsEqualTo(byName);
            // Every way a cursor could be applied to an ordering it was not issued for stamps apart.
            await Assert.That(CursorCodec.OrderStamp("Employee", [], [(new MemberNode(["Name"]), true)])).IsNotEqualTo(byName);
            await Assert.That(CursorCodec.OrderStamp("Employee", [], [(new MemberNode(["Id"]), false)])).IsNotEqualTo(byName);
            await Assert.That(CursorCodec.OrderStamp("Order", [], [(new MemberNode(["Name"]), false)])).IsNotEqualTo(byName);
            // The rows the keys are read off: a flatten or a narrowing changes them without changing
            // a key's spelling.
            await Assert.That(CursorCodec.OrderStamp("Employee", ["flatten Reports"], [(new MemberNode(["Name"]), false)])).IsNotEqualTo(byName);
            await Assert.That(CursorCodec.OrderStamp("Employee", ["narrow Manager"], [(new MemberNode(["Name"]), false)])).IsNotEqualTo(byName);
            await Assert.That(CursorCodec.OrderStamp("Employee", [], [(new MemberNode(["Name"]), false), (new MemberNode(["Id"]), false)])).IsNotEqualTo(byName);
        }
    }

    // The canonical form is encoded into a stack buffer, with a rented one past its size, so an
    // ordering long enough to need the second must stamp as stably as a short one.
    [Test]
    public async Task StampsAnOrderingTooLongForTheStackBuffer()
    {
        var keys = Enumerable.Range(0, 100)
            .Select(_ => (Key: (Node) new MemberNode([new string('m', 40) + _]), Descending: _ % 2 == 0))
            .ToArray();

        var stamp = CursorCodec.OrderStamp("Employee", [], keys);

        using (Assert.Multiple())
        {
            await Assert.That(stamp).Length().IsEqualTo(16);
            await Assert.That(CursorCodec.OrderStamp("Employee", [], keys)).IsEqualTo(stamp);
        }
    }
}
