/// <summary>
/// The disclosure audit over the answers that are made whole: what is recorded for each kind, that
/// the record is taken from what was written rather than beside it, and that turning it on changes
/// nothing about the answer itself.
/// </summary>
public class DisclosureAuditTests
{
    [Test]
    public async Task AListIsRecordedRowByRow()
    {
        var (processor, store) = Disclosures.Audited();

        await Disclosures.Buffered(processor, Disclosures.Names());

        await Verify(await Disclosures.Recorded(store));
    }

    [Test]
    public async Task EveryKindOfAnswerIsRecorded()
    {
        var (processor, store) = Disclosures.Audited();

        await Disclosures.Buffered(processor, Paged());
        await Disclosures.Buffered(processor, First("A"));
        await Disclosures.Buffered(processor, First("Z"));
        await Disclosures.Buffered(processor, Counted());

        await Verify(await Disclosures.Recorded(store));
    }

    // A processor asked directly hands back an object rather than writing bytes, and shapes each row
    // into a dictionary on the way. What it records has to be what the writer records: the same rows
    // at the same addresses, or one row would be two different contents depending on who asked.
    [Test]
    public async Task AskedDirectlyTheRecordIsTheWritersOwn()
    {
        var (processor, store) = Disclosures.Audited();
        QueryRequest[] requests = [Disclosures.Names(), Paged(), First("A"), First("Z"), Counted(), Avatars()];

        foreach (var request in requests)
        {
            await Disclosures.Buffered(processor, request);
            Disclosures.Direct(processor, request);
        }

        var events = await Disclosures.Events(store);
        await Assert.That(events).Count().IsEqualTo(requests.Length * 2);
        for (var index = 0; index < events.Count; index += 2)
        {
            var written = events[index];
            var direct = events[index + 1];
            using (Assert.Multiple())
            {
                await Assert.That(direct.Event.Kind).IsEqualTo(written.Event.Kind);
                await Assert.That(direct.Event.Request).IsEqualTo(written.Event.Request);
                await Assert.That(direct.Close!.Units).IsEqualTo(written.Close!.Units);
                await Assert.That(direct.Close.Response).IsEqualTo(written.Close.Response);
                await Assert.That(await Disclosures.Addresses(store, direct.Event.Id))
                    .IsEquivalentTo(await Disclosures.Addresses(store, written.Event.Id), CollectionOrdering.Matching);
                await Assert.That(direct.Event.Shape).IsEqualTo(written.Event.Shape);
            }
        }
    }

    // Three rows and the request that asked for them, kept once however often they are sent.
    [Test]
    public async Task ARowSentTwiceIsKeptOnce()
    {
        var (processor, store) = Disclosures.Audited();

        await Disclosures.Buffered(processor, Disclosures.Names());
        var afterOne = store.ContentCount;
        await Disclosures.Buffered(processor, Disclosures.Names());

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(afterOne).IsEqualTo(4);
            await Assert.That(store.ContentCount).IsEqualTo(4);
            await Assert.That(events).Count().IsEqualTo(2);
            await Assert.That(events[1].Close!.Response).IsEqualTo(events[0].Close!.Response);
            await Assert.That(await Disclosures.Addresses(store, events[1].Event.Id))
                .IsEquivalentTo(await Disclosures.Addresses(store, events[0].Event.Id), CollectionOrdering.Matching);
        }
    }

    // An address is nothing but a hash of the content and what kind of content it is, so anybody
    // holding the bytes can find the record of them — and nobody needs this library to check one.
    [Test]
    public async Task AnAddressIsTheHashOfTheKindAndTheBytes()
    {
        var (processor, store) = Disclosures.Audited();

        await Disclosures.Buffered(processor, Disclosures.Names());

        var first = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!.Units[0];
        byte[] tagged = [(byte) ScryDisclosureContentKind.Row, .. "{\"name\":\"Aaron\"}"u8];
        using (Assert.Multiple())
        {
            await Assert.That(Encoding.UTF8.GetString(first.Content.Bytes.Span)).IsEqualTo("{\"name\":\"Aaron\"}");
            await Assert.That(first.Content.Address).IsEqualTo(ScryDisclosureAddress.From(SHA256.HashData(tagged)));
        }
    }

    // Under a key an address is an HMAC, so one left behind after its content was erased cannot be
    // matched by hashing guesses at what the content was.
    [Test]
    public async Task AKeyMakesEveryAddressAnHmac()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var (processor, store) = Disclosures.Audited(configure: _ => _.AddressKey = key);

        await Disclosures.Buffered(processor, Disclosures.Names());

        var first = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!.Units[0];
        byte[] tagged = [(byte) ScryDisclosureContentKind.Row, .. "{\"name\":\"Aaron\"}"u8];
        using (Assert.Multiple())
        {
            await Assert.That(first.Content.Address).IsEqualTo(ScryDisclosureAddress.From(HMACSHA256.HashData(key, tagged)));
            await Assert.That(first.Content.Address).IsNotEqualTo(ScryDisclosureAddress.From(SHA256.HashData(tagged)));
        }
    }

    // The guard on the whole design: a row is written into a scratch buffer, hashed, and copied into
    // the response, and a row holding a binary value is written twice. Neither may move a byte.
    [Test]
    public async Task TheAnswerIsTheSameBytesWithTheAuditOnOrOff()
    {
        var (processor, _) = Disclosures.Audited();
        Dictionary<string, QueryRequest> requests = new()
        {
            ["list"] = Disclosures.Names(),
            ["page"] = Paged(),
            ["single"] = First("A"),
            ["none"] = First("Z"),
            ["scalar"] = Counted(),
            ["binary"] = Avatars(),
            ["default"] = QueryRequest.Create("Employee", [new OrderByOp(new MemberNode(["Name"]), Descending: false)]),
            ["nested"] = Nested(),
            ["grouped"] = Grouped(),
            ["distinct"] = Distinct(),
            ["poco"] = QueryRequest.Create("Holiday", [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]),

            // The shapes whose rows are identified by keys read beside what was asked for. None of
            // those keys may reach the answer.
            ["navigations"] = Disclosures.From<Employee>("Employee")
                .OrderBy(_ => _.Name)
                .Select(_ => new
                {
                    _.Name,
                    Manager = _.Manager!.Name,
                    Department = _.Department!.Name
                })
                .ToScryRequest(),
            ["joined"] = Disclosures.From<Employee>("Employee")
                .OrderBy(_ => _.Name)
                .Join(
                    Disclosures.From<Department>("Department"),
                    _ => _.DepartmentId,
                    _ => _.Id,
                    (employee, department) => new
                    {
                        Employee = employee.Name,
                        Department = department.Name
                    })
                .ToScryRequest(),
            ["flattened"] = Disclosures.From<Order>("Order")
                .SelectMany(_ => _.Lines)
                .OrderBy(_ => _.Sku)
                .Select(_ => new
                {
                    _.Sku
                })
                .ToScryRequest(),
            ["narrowed"] = Disclosures.From<Asset>("Asset")
                .OfType<Vehicle>()
                .OrderBy(_ => _.Name)
                .Select(_ => new
                {
                    _.Wheels
                })
                .ToScryRequest(),
            ["paged navigations"] = Disclosures.From<Employee>("Employee")
                .OrderBy(_ => _.Name)
                .Select(_ => new
                {
                    _.Name,
                    Department = _.Department!.Name
                })
                .ToScryRequest(new PageOp(Size: 3)),
            ["first navigations"] = Disclosures.From<Employee>("Employee")
                .OrderBy(_ => _.Name)
                .Select(_ => new
                {
                    _.Name,
                    Manager = _.Manager!.Name
                })
                .ToScryRequest(new FirstOp(OrDefault: true, Predicate: null))
        };

        foreach (var (name, request) in requests)
        {
            var off = await Disclosures.Buffered(SharedProcessor.Instance, request);
            var on = await Disclosures.Buffered(processor, request);
            await Assert.That(Comparable(on)).IsEqualTo(Comparable(off)).Because(name);
        }
    }

    // A page's cursor is sealed under a fresh nonce every time it is issued, so no two are alike
    // whether or not anything is recording. Everything around it is compared.
    static string Comparable(byte[] answer) =>
        Regex.Replace(Encoding.UTF8.GetString(answer), "\"cursor\":\"[^\"]*\"", "\"cursor\":\"\"");

    // The same, where the transport carries binary values as parts: the envelope holds an index where
    // the value was, and the parts hold the values. The audit writes such a row a second time for its
    // own record, and must not claim an index or a part while doing it.
    [Test]
    public async Task ABinaryPartIsStillThePartItWas()
    {
        var (processor, _) = Disclosures.Audited();
        var partsOff = new BinaryPartCollector();
        var partsOn = new BinaryPartCollector();

        var off = await Disclosures.Buffered(SharedProcessor.Instance, Avatars(), binary: partsOff);
        var on = await Disclosures.Buffered(processor, Avatars(), binary: partsOn);

        using (Assert.Multiple())
        {
            await Assert.That(Encoding.UTF8.GetString(on)).IsEqualTo(Encoding.UTF8.GetString(off));
            await Assert.That(Encoding.UTF8.GetString(on)).Contains("{\"$bin\":0}");
            await Assert.That(partsOn.Count).IsEqualTo(partsOff.Count);
            await Assert.That(partsOn.Parts.Select(Convert.ToHexString))
                .IsEquivalentTo(partsOff.Parts.Select(Convert.ToHexString), CollectionOrdering.Matching);
        }
    }

    // A binary value is named in its row by address and length. So the record of a row is the same
    // whether its bytes travelled inline or as a part, and a photograph does not enter the store
    // because somebody listed the people in it.
    [Test]
    public async Task ABinaryValueIsRecordedByItsDigest()
    {
        var (processor, store) = Disclosures.Audited();

        await Disclosures.Buffered(processor, Avatars());
        await Disclosures.Buffered(processor, Avatars(), binary: new());

        var events = await Disclosures.Events(store);
        var inline = (await store.Reconstruct(events[0].Event.Id))!.Units;
        byte[] tagged = [(byte) ScryDisclosureContentKind.Bytes, 0x0A, 0x0B];
        var digest = ScryDisclosureAddress.From(SHA256.HashData(tagged));
        var kept = await store.Content(digest);
        using (Assert.Multiple())
        {
            await Assert.That(Encoding.UTF8.GetString(inline[0].Content.Bytes.Span))
                .IsEqualTo($$$"""{"name":"Aaron","avatar":{"$bytes":"{{{digest}}}","length":2}}""");
            await Assert.That(await Disclosures.Addresses(store, events[1].Event.Id))
                .IsEquivalentTo(await Disclosures.Addresses(store, events[0].Event.Id), CollectionOrdering.Matching);
            await Assert.That(kept!.Value.Kind).IsEqualTo(ScryDisclosureContentKind.Bytes);
            await Assert.That(kept.Value.Length).IsEqualTo(2);
            await Assert.That(kept.Value.Held).IsFalse();
        }
    }

    [Test]
    public async Task ABinaryValueIsKeptWhereTheHostAsksForIt()
    {
        var (processor, store) = Disclosures.Audited(configure: _ => _.StoreBinaryContent = true);

        await Disclosures.Buffered(processor, Avatars());

        byte[] tagged = [(byte) ScryDisclosureContentKind.Bytes, 0x0A, 0x0B];
        var kept = await store.Content(ScryDisclosureAddress.From(SHA256.HashData(tagged)));
        using (Assert.Multiple())
        {
            await Assert.That(kept!.Value.Held).IsTrue();
            await Assert.That(Convert.ToHexString(kept.Value.Bytes.Span)).IsEqualTo("0A0B");
        }
    }

    // A row a policy filters never reaches a writer, so there is nothing of it to record: the audit
    // sees what the caller was sent, which is what the policies left.
    [Test]
    public async Task ARowAPolicyHidesIsNeverRecorded()
    {
        var (processor, store) = Disclosures.Audited(_ => _.AddPolicy<Employee, ActiveOnlyPolicy>());
        var everyone = QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);

        var answer = await Disclosures.Buffered(processor, everyone);

        var units = (await store.Reconstruct((await Disclosures.Events(store)).Single().Event.Id))!.Units;
        var names = units.Select(_ => Encoding.UTF8.GetString(_.Content.Bytes.Span)).ToList();
        using (Assert.Multiple())
        {
            await Assert.That(Encoding.UTF8.GetString(answer)).DoesNotContain("Bob");
            await Assert.That(names).IsEquivalentTo(
                ["{\"name\":\"Aaron\"}", "{\"name\":\"Alice\"}", "{\"name\":\"Carol\"}"],
                CollectionOrdering.Matching);
        }
    }

    // A recorded answer is one no cache may keep. A stored copy is read again with no request at
    // all, by the same caller or the next person at that browser, and nothing here would know.
    [Test]
    public async Task ARecordedAnswerMayNotBeStored()
    {
        var (processor, _) = Disclosures.Audited();
        var headers = new HeaderDictionary
        {
            ["Cache-Control"] = "private, no-cache"
        };

        await Disclosures.Buffered(processor, Disclosures.Names(), responseHeaders: headers);

        await Assert.That(headers["Cache-Control"].ToString()).IsEqualTo("no-store");
    }

    // A 304 sends nothing and runs nothing, so the rows a caller goes on reading from its own copy
    // could not be recorded. A host that asks for both is told at startup, not left with a short record.
    [Test]
    public async Task TheAuditRefusesToStartBesideConditionalAnswers()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Disclosures.Audited(_ => _.QueryFreshness = (_, _) => new("1")));

        await Assert.That(exception.Message).Contains("304");
    }

    [Test]
    public async Task ADriftedClientsEnvelopeIsWrittenBeforeItIsRecorded()
    {
        var (processor, store) = Disclosures.Audited();
        var stale = QueryRequest.Create("Employee", [.. Disclosures.Names().Pipeline], "stamp-from-an-older-model");

        await using var context = TestContext.CreateSeeded();
        var output = new ArrayBufferWriter<byte>();
        var fallback = await processor.TryExecuteBufferedAsync(
            stale,
            context,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            new HeaderDictionary(),
            output);

        // With the audit off this envelope is handed back for the caller to write. On, it is written
        // here: a transport's size limit refuses a response as it is written, and that has to happen
        // before the record of the answer is accepted.
        var answer = Encoding.UTF8.GetString(output.WrittenSpan);
        var recorded = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(fallback).IsNull();
            await Assert.That(answer).Contains("enumAliases");
            await Assert.That(answer).Contains("Aaron");
            await Assert.That(recorded.Single().Close!.Units).IsEqualTo(3);
        }
    }

    // Each entry of a batch is an answer of its own, recorded as one. An entry that was refused sent
    // nothing and records nothing.
    [Test]
    public async Task ABatchIsRecordedEntryByEntry()
    {
        var (processor, store) = Disclosures.Audited();
        var batch = QueryBatchRequest.Create(
        [
            Disclosures.Names(),
            QueryRequest.Create("Missing", [new CountOp()]),
            Counted()
        ]);

        await using var context = TestContext.CreateSeeded();
        var output = new ArrayBufferWriter<byte>();
        await processor.ExecuteBatchBufferedAsync(
            batch,
            context,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            new HeaderDictionary(),
            output,
            binary: null);
        var direct = processor.ExecuteBatch(batch, context);

        var events = await Disclosures.Events(store);
        using (Assert.Multiple())
        {
            await Assert.That(direct.Results).Count().IsEqualTo(3);
            await Assert.That(events.Select(_ => _.Event.Kind)).IsEquivalentTo(
                [ScryDisclosureKind.List, ScryDisclosureKind.Scalar, ScryDisclosureKind.List, ScryDisclosureKind.Scalar],
                CollectionOrdering.Matching);
        }
    }

    static QueryRequest Paged() =>
        QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new PageOp(Size: 2)
            ]);

    static QueryRequest First(string prefix) =>
        QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new CallNode(KnownFunction.StringStartsWith, new MemberNode(["Name"]), [new ConstNode(prefix, ClrTypeTag.String)])),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))])),
                new FirstOp(OrDefault: true, Predicate: null)
            ]);

    static QueryRequest Counted() =>
        QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new MemberNode(["Active"])),
                new CountOp()
            ]);

    static QueryRequest Avatars() =>
        QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(
                    new(
                    [
                        new("Name", new NodeValue(new MemberNode(["Name"]))),
                        new("Avatar", new NodeValue(new MemberNode(["Avatar"])))
                    ]))
            ]);

    static QueryRequest Nested() =>
        QueryRequest.Create(
            "Employee",
            [
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(
                    new(
                    [
                        new("Name", new NodeValue(new MemberNode(["Name"]))),
                        new("ManagerName", new NodeValue(new MemberNode(["Manager", "Name"]))),
                        new("Department", new NestedValue(
                            ["Department"],
                            new([new("Name", new NodeValue(new MemberNode(["Name"])))])))
                    ]))
            ]);

    static QueryRequest Grouped() =>
        QueryRequest.Create(
            "Order",
            [
                new GroupByOp([new MemberNode(["Region"])]),
                new SelectOp(
                    new(
                    [
                        new("Region", new NodeValue(new MemberNode(["Region"]))),
                        new("Total", new NodeValue(new AggregateNode(AggregateFn.Sum, new MemberNode(["Amount"])))),
                        new("Count", new NodeValue(new AggregateNode(AggregateFn.Count, Selector: null)))
                    ]))
            ]);

    static QueryRequest Distinct() =>
        QueryRequest.Create(
            "Employee",
            [
                new SelectOp(new([new("Status", new NodeValue(new MemberNode(["Status"])))])),
                new DistinctOp(),
                new OrderByOp(new MemberNode(["Status"]), Descending: false)
            ]);
}
