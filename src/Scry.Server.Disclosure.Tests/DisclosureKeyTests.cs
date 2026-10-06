/// <summary>
/// What a row is recorded by. The key comes from the model and not from what a caller may name, in
/// the key's own order; and a source the model gives no readable key has to be given one, or the host
/// has to say that it has none, before the server will start with the audit on.
/// </summary>
public class DisclosureKeyTests
{
    // A patient's id is hidden from every client. It is still read with every row and recorded, and
    // still never sent: the allow-list decides what a caller may see, not what the server knows.
    [Test]
    public async Task AKeyHiddenFromClientsIsStillWhatARowIsRecordedBy()
    {
        await using var database = await Clinic.Instance.Build();
        var (processor, store) = Audited();

        var answer = processor.Execute(Clinic.Names(), database.Context);
        var asked = Assert.ThrowsExactly<ScryValidationException>(
            () => processor.Execute(Clinic.All("Patient", "Id"), database.Context));

        using (Assert.Multiple())
        {
            await Assert.That(ScryJson.Serialize(answer)).DoesNotContain("\"id\"").IgnoringCase();
            await Assert.That(asked.Message).Contains("Id");
            await Assert.That(await Clinic.Recorded(store)).IsEquivalentTo(
                [
                    "{\"name\":\"Ada\"} <- Patient[1]",
                    "{\"name\":\"Brook\"} <- Patient[2]",
                    "{\"name\":\"Chidi\"} <- Patient[3]"
                ],
                CollectionOrdering.Matching);
        }
    }

    // Two values identify an admission, and are recorded in the order the key declares them — which is
    // the order a row is later asked about by.
    [Test]
    public async Task AKeyOfSeveralValuesIsRecordedInItsOwnOrder()
    {
        await using var database = await Clinic.Instance.Build();
        var (processor, store) = Audited();
        var request = QueryRequest.Create(
            "Admission",
            [
                new OrderByOp(new MemberNode(["Reason"]), Descending: false),
                new ThenByOp(new MemberNode(["PatientId"]), Descending: false),
                new SelectOp(new([new("Reason", new NodeValue(new MemberNode(["Reason"])))]))
            ]);

        processor.Execute(request, database.Context);

        var received = await store.ReceiversOf("Admission", [1, 2]).ToListAsync();
        using (Assert.Multiple())
        {
            await Assert.That(await Clinic.Recorded(store)).IsEquivalentTo(
                [
                    "{\"reason\":\"Cast removed\"} <- Admission[1,2]",
                    "{\"reason\":\"Fall\"} <- Admission[1,1]",
                    "{\"reason\":\"Fall\"} <- Admission[3,1]"
                ],
                CollectionOrdering.Matching);
            await Assert.That(received.Single().Event.Caller).IsEqualTo("dr.osei");
            await Assert.That(await store.ReceiversOf("Admission", [2, 1]).ToListAsync()).IsEmpty();
        }
    }

    // The census has no key and the notes have one nothing can read. Neither is a mistake, but a
    // record with no row to hang on is a decision for the host, so the server asks for one.
    [Test]
    public async Task ASourceWithNothingToNameARowByStopsTheServerStarting()
    {
        await using var database = await Clinic.Instance.Build();
        var (unanswered, _) = Audited();
        var (half, _) = Audited(_ => _.Unkeyed<Note>());
        var (answered, _) = Audited(
            _ =>
            {
                _.Unkeyed<Note>();
                _.Key<WardCensus>(_ => _.Ward);
            });
        var off = ScryProcessor.Create<ClinicContext>(_ => { });

        var note = Assert.ThrowsExactly<Exception>(() => unanswered.ValidateAgainstModel(database.Context));
        var census = Assert.ThrowsExactly<Exception>(() => half.ValidateAgainstModel(database.Context));
        answered.ValidateAgainstModel(database.Context);
        off.ValidateAgainstModel(database.Context);

        using (Assert.Multiple())
        {
            await Assert.That(note.Message).Contains("'Note' (Note) has nothing a row of it can be recorded by");
            await Assert.That(note.Message).Contains("_.Unkeyed<Note>()");
            await Assert.That(census.Message).Contains("'WardCensus' (WardCensus) has nothing a row of it can be recorded by");
            await Assert.That(census.Message).Contains("_.Key<WardCensus>(_ => _.Code)");
        }
    }

    // A key the host declares is what the rows are recorded by; a source acknowledged as having none
    // is recorded as what was sent, with no row to ask about.
    [Test]
    public async Task AHostSaysWhatIdentifiesARowWhereTheModelDoesNot()
    {
        await using var database = await Clinic.Instance.Build();
        var (processor, store) = Audited(
            _ =>
            {
                _.Unkeyed<Note>();
                _.Key<WardCensus>(_ => _.Ward);
            });

        processor.Execute(Clinic.All("WardCensus", "Patients"), database.Context);
        processor.Execute(Clinic.All("Note", "Text"), database.Context);

        using (Assert.Multiple())
        {
            await Assert.That(await Clinic.Recorded(store)).IsEquivalentTo(
                [
                    "{\"patients\":1} <- WardCensus[\"South\"]",
                    "{\"patients\":2} <- WardCensus[\"North\"]",
                    "{\"text\":\"Linen ordered\"} <- ",
                    "{\"text\":\"Night round done\"} <- "
                ],
                CollectionOrdering.Matching);
            await Assert.That(await store.ReceiversOf("WardCensus", ["North"]).ToListAsync()).Count().IsEqualTo(1);
        }
    }

    [Test]
    public async Task AKeyIsDeclaredAsMembersOfTheRow()
    {
        var computed = Assert.ThrowsExactly<ArgumentException>(() => Declare<WardCensus>(_ => _.Ward + "!"));
        var none = Assert.ThrowsExactly<ArgumentException>(() => Declare<WardCensus>());

        using (Assert.Multiple())
        {
            await Assert.That(computed.Message).Contains("a property read straight off the row");
            await Assert.That(none.Message).Contains("names at least one member");
        }
    }

    // A processor over the clinic with the audit on, recording to a store of its own.
    static (ScryProcessor Processor, ScryMemoryDisclosureStore Store) Audited(Action<ScryDisclosureOptions>? configure = null)
    {
        var store = new ScryMemoryDisclosureStore();
        var processor = ScryProcessor.Create<ClinicContext>(
            _ => _.UseDisclosureAudit(
                store,
                audit =>
                {
                    audit.Caller = _ => "dr.osei";
                    configure?.Invoke(audit);
                }));
        return (processor, store);
    }

    // The audit's settings are made by UseDisclosureAudit and nowhere else, so a test that wants to
    // hand them a key that should be refused goes through it.
    static void Declare<TSource>(params Expression<Func<TSource, object?>>[] members) =>
        ScryProcessor.Create<ClinicContext>(
            _ => _.UseDisclosureAudit(
                new ScryMemoryDisclosureStore(),
                audit => audit.Key(members)));
}
