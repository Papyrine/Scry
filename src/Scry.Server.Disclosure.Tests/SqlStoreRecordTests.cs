using static SqlStores;

/// <summary>
/// What the SQL Server store says of the answers that do not end the ordinary way, and of names that
/// differ by less than a database would usually notice. Each is recorded to the in-memory store too
/// and asked of both, from batches made by hand so that every part of one is the test's to choose.
/// </summary>
public class SqlStoreRecordTests
{
    static DateTimeOffset start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    static ScryDisclosureContent ada = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Ada\"}");
    static ScryDisclosureContent brook = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Brook\"}");
    static ScryDisclosureContent chidi = Clinic.Content(ScryDisclosureContentKind.Row, "{\"name\":\"Chidi\"}");
    static ScryDisclosureShape names = Clinic.Shape(new ScryDisclosureField("Patient", "Name", ScryDisclosureFieldUse.Returned, false));

    // An answer withdrawn after it was accepted, a stream cut short, one whose process stopped before
    // it could say how it ended, and one recorded against nobody. Moved on a batch at a time or all at
    // once, the record of them is the same, and is what the in-memory store says.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnAnswerThatDidNotEndTheOrdinaryWay(bool oneAtATime)
    {
        // A database for each way of running it: the two run side by side, and would otherwise be
        // given one name.
        await using var database = await Clinic.Instance.Build(databaseSuffix: $"{oneAtATime}");
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var both = new Both(memory, kept);
        var whole = Begin(1, ScryDisclosureKind.List);
        var withdrawn = Begin(2, ScryDisclosureKind.List);
        var cut = Begin(3, ScryDisclosureKind.Stream);
        var unfinished = Begin(4, ScryDisclosureKind.List);
        var nobody = Begin(5, ScryDisclosureKind.Single, caller: null);
        ScryDisclosureBatch[] batches =
        [
            Clinic.Part(whole.Id, 0, whole, names, Close(ScryDisclosureOutcome.Released, 2), (0, ada, "Patient", "[1]"), (1, brook, "Patient", "[2]")),

            // Accepted whole, and then a later step failed before anything of it left.
            Clinic.Part(withdrawn.Id, 0, withdrawn, names, Close(ScryDisclosureOutcome.Released, 1), (0, ada, "Patient", "[1]")),
            Clinic.Part(withdrawn.Id, 1, close: Close(ScryDisclosureOutcome.Retracted, 0)),

            // A stream: its header, two chunks, and a close that says the second chunk never left.
            Clinic.Part(cut.Id, 0, cut, names),
            Clinic.Part(cut.Id, 1, rows: [(0, ada, "Patient", "[1]"), (1, brook, "Patient", "[2]")]),
            Clinic.Part(cut.Id, 2, rows: [(2, chidi, "Patient", "[3]")]),
            Clinic.Part(cut.Id, 3, close: Close(ScryDisclosureOutcome.Truncated, 2)),

            // No close at all: the process stopped, and what it accepted may have gone.
            Clinic.Part(unfinished.Id, 0, unfinished, names, rows: [(0, chidi, "Patient", "[3]")]),
            Clinic.Part(nobody.Id, 0, nobody, names, Close(ScryDisclosureOutcome.Released, 1), (0, brook, "Patient", "[2]"))
        ];

        foreach (var batch in batches)
        {
            await both.AppendAsync(batch, Cancel.None);
            if (oneAtATime)
            {
                await kept.DrainAsync();
            }
        }

        await kept.DrainAsync();

        var told = await Told(kept, "dr.osei", null);
        var taken = (await kept.Reconstruct(withdrawn.Id))!;
        using (Assert.Multiple())
        {
            await Assert.That(told).IsEqualTo(await Told(memory, "dr.osei", null));
            await Assert.That(taken.Close!.Outcome).IsEqualTo(ScryDisclosureOutcome.Retracted);
            await Assert.That(taken.Units).Count().IsEqualTo(1);
            await Assert.That((await kept.Status()).Events).IsEqualTo(5);
            await Assert.That((await memory.Status()).Events).IsEqualTo(5);
            await Assert.That(await Count(database, "DisclosureContent")).IsEqualTo(3);
        }

        await Verify(told).IgnoreParametersForVerified();
    }

    // A binary value is recorded by its digest unless the host asked for it to be kept. Where the
    // bytes arrive later — the host changed its mind, another answer carried them — the content
    // already named takes them.
    [Test]
    public async Task ContentKnownByDigestTakesItsBytesWhenTheyArrive()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var both = new Both(memory, kept);
        var photo = Clinic.Content(ScryDisclosureContentKind.Bytes, "not really a photograph");
        var digest = photo with
        {
            Bytes = default
        };
        var first = Begin(1, ScryDisclosureKind.Attachment);
        var second = Begin(2, ScryDisclosureKind.Attachment);

        await both.AppendAsync(Clinic.Part(first.Id, 0, first, close: Close(ScryDisclosureOutcome.Released, 1), rows: [(0, digest, "Patient", "[1]")]), Cancel.None);
        await kept.DrainAsync();
        var before = await kept.Content(photo.Address);
        var toldBefore = await Told(kept);
        var saidBefore = await Told(memory);

        await both.AppendAsync(Clinic.Part(second.Id, 0, second, close: Close(ScryDisclosureOutcome.Released, 1), rows: [(0, photo, "Patient", "[1]")]), Cancel.None);
        await kept.DrainAsync();
        var after = await kept.Content(photo.Address);

        using (Assert.Multiple())
        {
            await Assert.That(before!.Value.Held).IsFalse();
            await Assert.That(before.Value.Length).IsEqualTo(23);
            await Assert.That(before.Value.Kind).IsEqualTo(ScryDisclosureContentKind.Bytes);
            await Assert.That(toldBefore).IsEqualTo(saidBefore);
            await Assert.That(toldBefore).Contains("0: (digest of 23) <- Patient[1] via ''");
            await Assert.That(Encoding.UTF8.GetString(after!.Value.Bytes.Span)).IsEqualTo("not really a photograph");
            await Assert.That(await Told(kept)).IsEqualTo(await Told(memory));
            await Assert.That(await kept.Content(ada.Address)).IsNull();
            await Assert.That(await memory.Content(ada.Address)).IsNull();
        }
    }

    // A caller, a source, a member and a key are the characters they are: two that differ by case, by
    // an accent or by a trailing space are two, whatever the database's collation makes of them, and
    // one far too long to be an index key is recorded whole and found again.
    [Test]
    public async Task NamesAreTheCharactersTheyAre()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var both = new Both(memory, kept);
        var endless = new string('k', 6000);
        string[] callers = ["dr.osei", "Dr.Osei", "dr.osei ", "dr.oséi", endless];
        (string Source, string Key)[] rows = [("Patient", "[\"a\"]"), ("patient", "[\"a\"]"), ("Patient", "[\"A\"]"), ("Patient", "[\"a \"]"), ("Patient", $"[\"{endless}\"]")];
        var minute = 0;
        foreach (var caller in callers)
        {
            foreach (var (source, key) in rows)
            {
                var shape = Clinic.Shape(new ScryDisclosureField(source, "Name", ScryDisclosureFieldUse.Returned, false));
                var begin = new ScryDisclosureEvent(Guid.CreateVersion7(), start.AddMinutes(minute++), ScryDisclosureKind.Single, source)
                {
                    Caller = caller,
                    Shape = shape.Address
                };
                await both.AppendAsync(Clinic.Part(begin.Id, 0, begin, shape, Close(ScryDisclosureOutcome.Released, 1), (0, ada, source, key)), Cancel.None);
            }
        }

        await kept.DrainAsync();

        foreach (var store in new IScryDisclosureReader[] {memory, kept})
        {
            using (Assert.Multiple())
            {
                foreach (var caller in callers)
                {
                    var received = await store.ReceivedBy(caller, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync();
                    await Assert.That(received.Select(_ => _.Event.Caller!).Distinct()).IsEquivalentTo([caller]);
                    await Assert.That(received).Count().IsEqualTo(5);
                    await Assert.That(await store.MemberReceivedBy(caller, "patient", "Name").ToListAsync()).Count().IsEqualTo(1);
                    await Assert.That(await store.MemberReceivedBy(caller, "Patient", "name").ToListAsync()).IsEmpty();
                }

                await Assert.That(await store.ReceivedBy("DR.OSEI", DateTimeOffset.MinValue, DateTimeOffset.MaxValue).ToListAsync()).IsEmpty();
                await Assert.That(await store.ReceiversOf("Patient", ["a"]).ToListAsync()).Count().IsEqualTo(5);
                await Assert.That(await store.ReceiversOf("patient", ["a"]).ToListAsync()).Count().IsEqualTo(5);
                await Assert.That(await store.ReceiversOf("Patient", ["A"]).ToListAsync()).Count().IsEqualTo(5);
                await Assert.That(await store.ReceiversOf("Patient", ["a "]).ToListAsync()).Count().IsEqualTo(5);
                await Assert.That(await store.ReceiversOf("Patient", [endless]).ToListAsync()).Count().IsEqualTo(5);
                await Assert.That(await store.ReceiversOf("PATIENT", ["a"]).ToListAsync()).IsEmpty();
                await Assert.That((await store.Catalog()).Sources.Select(_ => _.Name)).IsEquivalentTo(["Patient", "patient"], CollectionOrdering.Matching);
            }
        }

        // Erased by the one name, and nothing recorded under its neighbours goes with it — except the
        // content itself, which here every one of them was sent, and erasure wins.
        var erased = await kept.EraseAsync("patient", ["a"], by: "records.officer");
        await Assert.That(erased.Units).IsEqualTo(1);
    }

    // An answer of a thousand rows, the same answer with one row changed, and the same with a row
    // more near its start. Each is a list of its own, and the lists are mostly the same runs: what
    // the second and third add is the run the difference is in, and not a thousand units again. A
    // row more moves every unit after it along by one, and the runs after it are still the runs
    // they were. Read back, each answer is every one of its units in its place.
    [Test]
    public async Task AnswersThatDifferByARowShareTheRest()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var both = new Both(memory, kept);

        var patients = Enumerable.Range(1, 1000).ToList();
        var changed = patients.ToList();
        changed[500] = 5000;
        var longer = patients.ToList();
        longer.Insert(10, 6000);

        var first = Begin(1, ScryDisclosureKind.List);
        await both.AppendAsync(Answer(first, patients), Cancel.None);
        await kept.DrainAsync();
        var once = await Count(database, "DisclosureRunUnit");
        var lines = await Count(database, "DisclosureManifestRun");

        var second = Begin(2, ScryDisclosureKind.List);
        var third = Begin(3, ScryDisclosureKind.List);
        var again = Begin(4, ScryDisclosureKind.List);
        await both.AppendAsync(Answer(second, changed), Cancel.None);
        await both.AppendAsync(Answer(third, longer), Cancel.None);
        await both.AppendAsync(Answer(again, patients), Cancel.None);
        await kept.DrainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(once).IsEqualTo(1000);
            await Assert.That(lines).IsGreaterThan(8);

            // A run is at most 256 units, and a difference can touch the run it is in and the one after.
            await Assert.That(await Count(database, "DisclosureRunUnit")).IsLessThanOrEqualTo(once + 2 * 512);
            await Assert.That(await Count(database, "DisclosureManifest")).IsEqualTo(3);
            await Assert.That(await Count(database, "DisclosureAnswer")).IsEqualTo(4);
            await Assert.That(await Count(database, "DisclosureUnit")).IsEqualTo(0);
            await Assert.That(await Count(database, "DisclosureUnits")).IsEqualTo(4001);
            foreach (var answer in (ScryDisclosureEvent[]) [first, second, third, again])
            {
                await Assert.That(await Units(kept, answer.Id)).IsEqualTo(await Units(memory, answer.Id));
            }

            await Assert.That(await Places(kept, 501)).IsEqualTo(await Places(memory, 501));
            await Assert.That(await Places(kept, 5000)).IsEqualTo(await Places(memory, 5000));
            await Assert.That(await Places(kept, 6000)).IsEqualTo(await Places(memory, 6000));
            await Assert.That(await Places(kept, 1000)).IsEqualTo("999, 999, 1000, 999");
        }
    }

    // A stream's chunks are lists too, each at the place in the answer it starts. The same stream
    // read again adds an event and a line for each chunk, and no units.
    [Test]
    public async Task AStreamGivenAgainAddsNoUnits()
    {
        await using var database = await Clinic.Instance.Build();
        var memory = new ScryMemoryDisclosureStore();
        await using var kept = Store(database);
        var both = new Both(memory, kept);
        var patients = Enumerable.Range(1, 300).ToList();

        var first = Begin(1, ScryDisclosureKind.Stream);
        var second = Begin(2, ScryDisclosureKind.Stream);
        foreach (var begin in (ScryDisclosureEvent[]) [first, second])
        {
            await both.AppendAsync(Clinic.Part(begin.Id, 0, begin, names), Cancel.None);
            await both.AppendAsync(Clinic.Part(begin.Id, 1, rows: Rows(patients, 0, 200)), Cancel.None);
            await both.AppendAsync(Clinic.Part(begin.Id, 2, rows: Rows(patients, 200, 100)), Cancel.None);
            await both.AppendAsync(Clinic.Part(begin.Id, 3, close: Close(ScryDisclosureOutcome.Released, 300)), Cancel.None);
            await kept.DrainAsync();
        }

        using (Assert.Multiple())
        {
            await Assert.That(await Count(database, "DisclosureRunUnit")).IsEqualTo(300);
            await Assert.That(await Count(database, "DisclosureManifest")).IsEqualTo(2);
            await Assert.That(await Count(database, "DisclosureAnswer")).IsEqualTo(4);
            await Assert.That(await Count(database, "DisclosureUnit")).IsEqualTo(0);
            await Assert.That(await Units(kept, second.Id)).IsEqualTo(await Units(memory, second.Id));
            await Assert.That(await Places(kept, 250)).IsEqualTo("249, 249");
        }
    }

    static ScryDisclosureBatch Answer(ScryDisclosureEvent begin, List<int> patients) =>
        Clinic.Part(begin.Id, 0, begin, names, Close(ScryDisclosureOutcome.Released, patients.Count), Rows(patients, 0, patients.Count));

    // The rows of a part of an answer: each patient at its place in the whole of it.
    static (int Ordinal, ScryDisclosureContent Content, string? Source, string? Key)[] Rows(List<int> patients, int from, int count) =>
    [
        .. patients
            .Skip(from)
            .Take(count)
            .Select((patient, index) => (
                from + index,
                Clinic.Content(ScryDisclosureContentKind.Row, $"{{\"name\":\"Patient {patient}\"}}"),
                (string?) "Patient",
                (string?) $"[{patient}]"))
    ];

    // An answer as a store gives it back: every unit's place, content and rows.
    static async Task<string> Units(IScryDisclosureReader reader, Guid id)
    {
        var answer = (await reader.Reconstruct(id))!;
        return string.Join('\n', answer.Units.Select(_ => $"{_.Ordinal} {_.Content.Address} {string.Join(',', _.Entities.Select(_ => _.Key))}"));
    }

    // Where in each answer a patient was, oldest answer first.
    static async Task<string> Places(IScryDisclosureReader reader, int patient)
    {
        var received = await reader.ReceiversOf("Patient", [patient]).ToListAsync();
        return string.Join(", ", received.OrderBy(_ => _.Event.At).Select(_ => _.Ordinal));
    }

    static ScryDisclosureEvent Begin(int minute, ScryDisclosureKind kind, string? caller = "dr.osei") =>
        new(Guid.CreateVersion7(), start.AddMinutes(minute), kind, "Patient")
        {
            Caller = caller,
            Shape = names.Address,
            Node = "ward-1"
        };

    static ScryDisclosureClose Close(ScryDisclosureOutcome outcome, int units) =>
        new(outcome, units)
        {
            At = start.AddHours(1)
        };
}
