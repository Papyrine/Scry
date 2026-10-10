/// <summary>
/// The disclosure audit's record on its own: the one format every sink is handed, and what the
/// in-memory store keeps and answers from it. Batches here are made by hand, so each case says exactly
/// what was recorded without a query in the way.
/// </summary>
public class DisclosureStoreTests
{
    static DateTimeOffset noon = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    // Everything a batch can carry, written to bytes and read back. What a journal file and an outbox
    // column hold is this, so a field that did not survive would be a field no durable store has.
    [Test]
    public async Task ABatchSurvivesBeingWrittenAndRead()
    {
        var row = Content(ScryDisclosureContentKind.Row, "{\"name\":\"Aaron\"}");
        var digest = new ScryDisclosureContent(Address(9, "bytes"), ScryDisclosureContentKind.Bytes, 2048, default);
        var shape = new ScryDisclosureShape(
            Address(4, "shape"),
            [
                new("Employee", "Name", ScryDisclosureFieldUse.Returned, Sensitive: false),
                new("Employee", "Avatar", ScryDisclosureFieldUse.Returned, Sensitive: true),
                new("Employee", "Active", ScryDisclosureFieldUse.Read, Sensitive: false)
            ]);
        var id = new Guid("0198a000-0000-7000-8000-000000000001");
        var batch = new ScryDisclosureBatch
        {
            EventId = id,
            Sequence = 3,
            Begin = new(id, noon, ScryDisclosureKind.Page, "Employee")
            {
                Caller = "alice",
                Subscribed = true,
                Delivery = ScryDisclosureDelivery.Confirmed,
                Request = Address(3, "request"),
                Shape = shape.Address,
                Sensitive = true,
                Stamp = "a-stamp",
                Correlation = "trace-1/2",
                Node = "node-1"
            },
            Shape = shape,
            Units = [new(0, row.Address), new(1, digest.Address)],
            Entities =
            [
                new(0, 0, "Employee", "[2]", ""),
                new(0, 1, "Department", "[1]", "Department"),
                new(1, 0, "Employee", "[\"A\",7]", "")
            ],
            Contents = [row, digest],
            Close = new(ScryDisclosureOutcome.Truncated, 1)
            {
                At = noon.AddSeconds(2),
                Response = Address(1, "response")
            },
            Review = new(new("0198a000-0000-7000-8000-000000000002"), noon.AddMinutes(5), ScryDisclosureQuestion.Export)
            {
                Reviewer = "dana",
                Parameters = Address(10, "parameters"),
                Results = 12,
                Events = [id],
                Node = "node-2"
            }
        };

        var bytes = new ArrayBufferWriter<byte>();
        batch.Serialize(bytes);
        var read = ScryDisclosureBatch.Deserialize(bytes.WrittenSpan);

        await Verify(Laid(read)).DontScrubGuids().DontScrubDateTimes();
    }

    // An empty batch is still a batch, and null where a batch may be null stays null.
    [Test]
    public async Task ABatchWithNothingOptionalSurvivesToo()
    {
        var id = Guid.CreateVersion7();
        var bytes = new ArrayBufferWriter<byte>();
        new ScryDisclosureBatch
        {
            EventId = id,
            Begin = new(id, noon, ScryDisclosureKind.Schema, "")
        }.Serialize(bytes);

        var read = ScryDisclosureBatch.Deserialize(bytes.WrittenSpan);

        using (Assert.Multiple())
        {
            await Assert.That(read.EventId).IsEqualTo(id);
            await Assert.That(read.Sequence).IsEqualTo(0);
            await Assert.That(read.Begin!.Caller).IsNull();
            await Assert.That(read.Begin.Request).IsNull();
            await Assert.That(read.Begin.Source).IsEqualTo("");
            await Assert.That(read.Shape).IsNull();
            await Assert.That(read.Close).IsNull();
            await Assert.That(read.Review).IsNull();
            await Assert.That(read.Units).IsEmpty();
            await Assert.That(read.Entities).IsEmpty();
            await Assert.That(read.Contents).IsEmpty();
        }
    }

    // What is stored has to refuse to be read as something else: a file cut short, or one that was
    // never a batch.
    [Test]
    public async Task BytesThatAreNotABatchAreRefused()
    {
        var bytes = new ArrayBufferWriter<byte>();
        var id = Guid.CreateVersion7();
        new ScryDisclosureBatch
        {
            EventId = id,
            Begin = new(id, noon, ScryDisclosureKind.List, "Employee")
        }.Serialize(bytes);
        var whole = bytes.WrittenSpan.ToArray();
        var foreign = whole.ToArray();
        foreign[0] ^= 0xFF;

        using (Assert.Multiple())
        {
            await Assert.That(Refused(whole[..^3])).IsTrue();
            await Assert.That(Refused(foreign)).IsTrue();
            await Assert.That(Refused([])).IsTrue();
        }
    }

    static bool Refused(byte[] bytes)
    {
        try
        {
            ScryDisclosureBatch.Deserialize(bytes);
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    [Test]
    public async Task AnAddressReadsBackFromItsText()
    {
        var address = Address(1, "anything");
        var text = address.ToString();

        using (Assert.Multiple())
        {
            await Assert.That(text.Length).IsEqualTo(43);
            await Assert.That(ScryDisclosureAddress.TryParse(text, out var parsed)).IsTrue();
            await Assert.That(parsed).IsEqualTo(address);
            await Assert.That(parsed.GetHashCode()).IsEqualTo(address.GetHashCode());
            await Assert.That(Convert.ToHexString(parsed.ToArray())).IsEqualTo(Convert.ToHexString(address.ToArray()));
            await Assert.That(ScryDisclosureAddress.TryParse("not an address", out _)).IsFalse();
            await Assert.That(ScryDisclosureAddress.TryParse(text[..42], out _)).IsFalse();
            await Assert.That(ScryDisclosureAddress.TryParse(null, out _)).IsFalse();
        }
    }

    // A journal ships a batch again after a crash it cannot tell from a success, so every store takes
    // the same batch twice and keeps it once.
    [Test]
    public async Task ABatchHandedOverTwiceIsKeptOnce()
    {
        var store = new ScryMemoryDisclosureStore();
        var (id, batch) = Answer("alice", noon, Row(2, "Aaron"), Row(1, "Alice"));

        store.Append(batch);
        store.Append(batch);

        var answer = (await store.Reconstruct(id))!;
        using (Assert.Multiple())
        {
            await Assert.That(answer.Units).Count().IsEqualTo(2);
            await Assert.That(answer.Units.SelectMany(_ => _.Entities)).Count().IsEqualTo(2);
            await Assert.That((await store.Status()).Events).IsEqualTo(1);
            await Assert.That(await store.ReceiversOf("Employee", [2]).ToListAsync()).Count().IsEqualTo(1);
        }
    }

    // Who received a row, and which version: every answer that carried it, the address of what was
    // sent each time, and only as far as each answer really got.
    [Test]
    public async Task WhoReceivedARow()
    {
        var store = new ScryMemoryDisclosureStore();
        var (first, one) = Answer("alice", noon, Row(2, "Aaron"), Row(1, "Alice"));
        var (second, two) = Answer("bob", noon.AddHours(1), Row(2, "Aaron B."));
        var (third, three) = Answer("carol", noon.AddHours(2), released: 1, Row(1, "Alice"), Row(2, "Aaron B."));
        store.Append(one);
        store.Append(two);
        store.Append(three);

        var received = await store.ReceiversOf("Employee", [2]).ToListAsync();
        var afternoon = await store.ReceiversOf("Employee", [2], from: noon.AddMinutes(30)).ToListAsync();

        // Carol's answer was cut short after its first row, so the second — this one — never left.
        using (Assert.Multiple())
        {
            await Assert.That(received.Select(_ => _.Event.Caller!)).IsEquivalentTo(["bob", "alice"], CollectionOrdering.Matching);
            await Assert.That(received.Select(_ => _.Event.Id)).IsEquivalentTo([second, first], CollectionOrdering.Matching);
            await Assert.That(received[0].Content).IsNotEqualTo(received[1].Content);
            await Assert.That(received[1].Ordinal).IsEqualTo(0);
            await Assert.That(afternoon.Select(_ => _.Event.Caller!)).IsEquivalentTo(["bob"], CollectionOrdering.Matching);
            await Assert.That(await store.ReceiversOf("Employee", [99]).ToListAsync()).IsEmpty();
            await Assert.That((await store.Reconstruct(third))!.Units).Count().IsEqualTo(2);
        }
    }

    [Test]
    public async Task WhatACallerReceived()
    {
        var store = new ScryMemoryDisclosureStore();
        var (early, one) = Answer("alice", noon, Row(1, "Alice"));
        var (late, two) = Answer("alice", noon.AddHours(2), Row(2, "Aaron"));
        var (_, other) = Answer("bob", noon.AddHours(1), Row(1, "Alice"));
        store.Append(one);
        store.Append(two);
        store.Append(other);

        var all = await store.ReceivedBy("alice", noon, noon.AddDays(1)).ToListAsync();
        var morning = await store.ReceivedBy("alice", noon, noon.AddHours(1)).ToListAsync();
        var older = await store.ReceivedBy("alice", noon, noon.AddDays(1), after: new(all[0].Event.At, all[0].Event.Id)).ToListAsync();
        var rebuilt = (await store.Reconstruct(late))!;

        using (Assert.Multiple())
        {
            // Newest first, and a cursor takes up after the last one read.
            await Assert.That(all.Select(_ => _.Event.Id)).IsEquivalentTo([late, early], CollectionOrdering.Matching);
            await Assert.That(morning.Select(_ => _.Event.Id)).IsEquivalentTo([early], CollectionOrdering.Matching);
            await Assert.That(older.Select(_ => _.Event.Id)).IsEquivalentTo([early], CollectionOrdering.Matching);
            await Assert.That(Encoding.UTF8.GetString(rebuilt.Units.Single().Content.Bytes.Span)).IsEqualTo("{\"id\":2,\"name\":\"Aaron\"}");
            await Assert.That(await store.Reconstruct(Guid.NewGuid())).IsNull();
        }
    }

    // Whether a caller was ever sent a member: its shape has to say the member was returned, and a row
    // of that source has to have left. Being filtered by a member is not being sent it.
    [Test]
    public async Task WhetherACallerWasEverSentAMember()
    {
        var store = new ScryMemoryDisclosureStore();
        var (sent, one) = Answer("alice", noon, Row(1, "Alice"));
        var (_, nothing) = Answer("alice", noon.AddHours(1));
        store.Append(one);
        store.Append(nothing);

        using (Assert.Multiple())
        {
            await Assert.That((await store.MemberReceivedBy("alice", "Employee", "Name").ToListAsync()).Select(_ => _.Event.Id))
                .IsEquivalentTo([sent], CollectionOrdering.Matching);
            await Assert.That(await store.MemberReceivedBy("alice", "Employee", "Active").ToListAsync()).IsEmpty();
            await Assert.That(await store.MemberReceivedBy("alice", "Employee", "Salary").ToListAsync()).IsEmpty();
            await Assert.That(await store.MemberReceivedBy("bob", "Employee", "Name").ToListAsync()).IsEmpty();
        }
    }

    // The close written last is the one that stands: an answer accepted whole and then not sent is
    // closed a second time, and it is that close a reader goes by.
    [Test]
    public async Task ALaterCloseReplacesAnEarlierOne()
    {
        var store = new ScryMemoryDisclosureStore();
        var (id, batch) = Answer("alice", noon, Row(1, "Alice"));
        var withdrawal = new ScryDisclosureBatch
        {
            EventId = id,
            Sequence = 1,
            Close = new(ScryDisclosureOutcome.Retracted, 0)
        };

        // Out of order, as a store fed from two places may see them.
        store.Append(withdrawal);
        store.Append(batch);

        using (Assert.Multiple())
        {
            await Assert.That((await store.Reconstruct(id))!.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Retracted);
            await Assert.That(await store.ReceivedBy("alice", noon, noon).ToListAsync()).IsEmpty();
            await Assert.That(await store.ReceiversOf("Employee", [1]).ToListAsync()).IsEmpty();
        }
    }

    // Erasing a row removes what was sent of it and keeps that it was sent: the events, the addresses
    // and the key all stay, and the content is gone from every answer that carried it.
    [Test]
    public async Task ErasingARowRemovesItsContentAndKeepsTheRecord()
    {
        var store = new ScryMemoryDisclosureStore(new SteppingClock());
        var (first, one) = Answer("alice", noon, Row(2, "Aaron"), Row(1, "Alice"));
        var (second, two) = Answer("bob", noon.AddHours(1), Row(2, "Aaron"));
        store.Append(one);
        store.Append(two);

        var erasure = await store.EraseAsync("Employee", [2], by: "dana");

        var kept = (await store.Reconstruct(first))!.Units;
        var gone = (await store.Reconstruct(second))!.Units.Single();
        using (Assert.Multiple())
        {
            await Assert.That(erasure.Units).IsEqualTo(1);
            await Assert.That(erasure.By).IsEqualTo("dana");
            await Assert.That(erasure.Key).IsEqualTo("[2]");
            await Assert.That(kept[0].Erased).IsTrue();
            await Assert.That(kept[0].Content.Held).IsFalse();
            await Assert.That(kept[0].Content.Length).IsEqualTo(23);
            await Assert.That(kept[1].Erased).IsFalse();
            await Assert.That(kept[1].Content.Held).IsTrue();
            await Assert.That(gone.Erased).IsTrue();
            await Assert.That(await store.ReceiversOf("Employee", [2]).ToListAsync()).Count().IsEqualTo(2);
            await Assert.That((await store.Erasures().ToListAsync()).Single()).IsEqualTo(erasure);
            await Assert.That((await store.EraseAsync("Employee", [2], by: "dana")).Units).IsEqualTo(0);
        }
    }

    // The same row sent again after it was erased is a new disclosure, and is kept as one.
    [Test]
    public async Task ARowSentAgainAfterErasureIsKeptAgain()
    {
        var store = new ScryMemoryDisclosureStore();
        var (_, before) = Answer("alice", noon, Row(2, "Aaron"));
        store.Append(before);
        await store.EraseAsync("Employee", [2], by: null);

        // Handed over again, the batch that was erased brings nothing back.
        store.Append(before);
        var (again, after) = Answer("bob", noon.AddHours(1), Row(2, "Aaron"));
        var stillGone = (await store.ReceiversOf("Employee", [2]).ToListAsync()).Single();
        var erased = (await store.Reconstruct(stillGone.Event.Id))!.Units.Single().Erased;
        store.Append(after);

        using (Assert.Multiple())
        {
            await Assert.That(erased).IsTrue();
            await Assert.That((await store.Reconstruct(again))!.Units.Single().Content.Held).IsTrue();
        }
    }

    // Reading the record is a disclosure too. A reviewer shown an event is, from then on, among those
    // who received its rows.
    [Test]
    public async Task AReviewerShownAnEventReceivedItsRows()
    {
        var store = new ScryMemoryDisclosureStore();
        var (id, batch) = Answer("alice", noon, Row(2, "Aaron"));
        store.Append(batch);
        var review = new ScryDisclosureReview(Guid.CreateVersion7(), noon.AddDays(1), ScryDisclosureQuestion.Event)
        {
            Reviewer = "dana",
            Results = 1,
            Events = [id]
        };
        var asked = new ScryDisclosureBatch
        {
            EventId = review.Id,
            Review = review
        };

        store.Append(asked);
        store.Append(asked);

        var received = await store.ReceiversOf("Employee", [2]).ToListAsync();
        using (Assert.Multiple())
        {
            await Assert.That(await store.Reviews().ToListAsync()).Count().IsEqualTo(1);
            await Assert.That(received).Count().IsEqualTo(2);
            await Assert.That(received[0].Review!.Reviewer).IsEqualTo("dana");
            await Assert.That(received[0].Event.Id).IsEqualTo(id);
            await Assert.That(received[1].Review).IsNull();
            await Assert.That((await store.Status()).Events).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheCatalogListsWhatWasRecorded()
    {
        var store = new ScryMemoryDisclosureStore();
        store.Append(Answer("alice", noon, Row(1, "Alice")).Batch);

        await Verify(await store.Catalog());
    }

    // A key is asked about as the text it was recorded as, whatever it is made of.
    [Test]
    public async Task AKeyIsWrittenAsTheResponseWritesItsValues()
    {
        var id = new Guid("0198a000-0000-7000-8000-000000000003");

        using (Assert.Multiple())
        {
            await Assert.That(ScryDisclosureEntity.KeyOf([5])).IsEqualTo("[5]");
            await Assert.That(ScryDisclosureEntity.KeyOf(["A", 7L])).IsEqualTo("[\"A\",7]");
            await Assert.That(ScryDisclosureEntity.KeyOf([id])).IsEqualTo("[\"0198a000-0000-7000-8000-000000000003\"]");
            await Assert.That(ScryDisclosureEntity.KeyOf([null])).IsEqualTo("[null]");
        }
    }

    static ScryDisclosureAddress Address(byte kind, string text)
    {
        byte[] tagged = [kind, .. Encoding.UTF8.GetBytes(text)];
        return ScryDisclosureAddress.From(SHA256.HashData(tagged));
    }

    static ScryDisclosureContent Content(ScryDisclosureContentKind kind, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(Address((byte) kind, text), kind, bytes.Length, bytes);
    }

    static (int Id, string Json) Row(int id, string name) =>
        (id, $$"""{"id":{{id}},"name":"{{name}}"}""");

    static (Guid Id, ScryDisclosureBatch Batch) Answer(string caller, DateTimeOffset at, params (int Id, string Json)[] rows) =>
        Answer(caller, at, rows.Length, rows);

    // One answer of employee rows, as the capture would hand it over: each row a unit with its content
    // and the key of the row it was read from, under a shape saying Id and Name were returned and
    // Active was only read.
    static (Guid Id, ScryDisclosureBatch Batch) Answer(string caller, DateTimeOffset at, int released, params (int Id, string Json)[] rows)
    {
        var id = Guid.CreateVersion7(at);
        var shape = new ScryDisclosureShape(
            Address(4, "employee-names"),
            [
                new("Employee", "Id", ScryDisclosureFieldUse.Returned, Sensitive: false),
                new("Employee", "Name", ScryDisclosureFieldUse.Returned, Sensitive: false),
                new("Employee", "Active", ScryDisclosureFieldUse.Read, Sensitive: false)
            ]);
        var contents = rows.Select(_ => Content(ScryDisclosureContentKind.Row, _.Json)).ToList();
        var outcome = ScryDisclosureOutcome.Released;
        if (released < rows.Length)
        {
            outcome = ScryDisclosureOutcome.Truncated;
        }

        return (
            id,
            new()
            {
                EventId = id,
                Begin = new(id, at, ScryDisclosureKind.List, "Employee")
                {
                    Caller = caller,
                    Shape = shape.Address,
                    Node = "node-1"
                },
                Shape = shape,
                Units = [.. contents.Select((content, ordinal) => new ScryDisclosureUnit(ordinal, content.Address))],
                Entities = [.. rows.Select((row, ordinal) => new ScryDisclosureEntity(ordinal, 0, "Employee", $"[{row.Id}]", ""))],
                Contents = contents,
                Close = new(outcome, released)
                {
                    At = at.AddSeconds(1)
                }
            });
    }

    static object Laid(ScryDisclosureBatch batch) =>
        new
        {
            batch.EventId,
            batch.Sequence,
            batch.Begin,
            ShapeAddress = batch.Shape?.Address,
            batch.Shape?.Fields,
            batch.Units,
            batch.Entities,
            Contents = batch.Contents.Select(_ => new
            {
                _.Address,
                _.Kind,
                _.Length,
                _.Held,
                Text = Encoding.UTF8.GetString(_.Bytes.Span)
            }),
            Close = new
            {
                batch.Close?.Outcome,
                batch.Close?.Units,
                batch.Close?.At,
                batch.Close?.Response
            },
            Review = new
            {
                batch.Review?.Id,
                batch.Review?.At,
                batch.Review?.Question,
                batch.Review?.Reviewer,
                batch.Review?.Parameters,
                batch.Review?.Results,
                batch.Review?.Events,
                batch.Review?.Node
            }
        };
}
