/// <summary>
/// Which attachments the explorer offers to fetch from a result, and which it refuses to. The
/// explorer never materializes a row into a model, so this stands in for the plan a generated client
/// builds while it translates — and has to agree with it.
/// </summary>
public class AttachmentLinkerTests
{
    // Mirrors the Contract fixture the server-side tests use: a key, an ordinary member, and an
    // attachment that is never a value. Employee is here to be the source that has none.
    static ScryIntrospection introspection = new(
        ScryIntrospection.CurrentVersion,
        MaxPageSize: 200,
        Sources:
        [
            new("Contract", "EfCore", "ContractQueryModel"),
            new("Employee", "EfCore", "EmployeeQueryModel"),
            new("Sealed", "EfCore", "SealedContractQueryModel")
        ],
        Types:
        [
            new("ContractQueryModel",
            [
                new("Id", "int", NeedsNullDefault: false, IsNavigation: false),
                new("Name", "string", NeedsNullDefault: true, IsNavigation: false),
                new("Document", "global::Scry.ScryAttachment", NeedsNullDefault: true, IsNavigation: false)
                {
                    IsAttachment = true,
                    ContentType = "application/pdf"
                }
            ])
            {
                Keys = ["Id"]
            },
            new("EmployeeQueryModel",
            [
                new("Name", "string", NeedsNullDefault: true, IsNavigation: false)
            ]),
            // Declares nothing of its own: the attachment is the base's. The key still travels here
            // — the server derives it for every type whose members, inherited ones included, hold an
            // attachment.
            new("SealedContractQueryModel", [])
            {
                Base = "ContractQueryModel",
                Keys = ["Id"]
            }
        ],
        Enums: []);

    static QueryRequest Request(params QueryOp[] pipeline) =>
        QueryRequest.Create("Contract", pipeline);

    // What the client's entry point sends for a query that wrote no Select: the model's own scalar
    // members, attachment excluded.
    static SelectOp WholeModel =>
        new(new(
        [
            new("Id", new NodeValue(new MemberNode(["Id"]))),
            new("Name", new NodeValue(new MemberNode(["Name"])))
        ]));

    [Test]
    public async Task LinksAWholeModelQuery()
    {
        var links = AttachmentLinker.Link(introspection, Request(WholeModel));

        await Assert.That(links).Count().IsEqualTo(1);
        using (Assert.Multiple())
        {
            await Assert.That(links[0].Root).IsEqualTo("Contract");
            await Assert.That(links[0].Member).IsEqualTo("Document");
            // Camel-cased: the response is keyed by ScryJson's dictionary policy, and the table's
            // columns are the response's own property names.
            await Assert.That(links[0].KeyColumns).IsEquivalentTo(["id"], CollectionOrdering.Matching);
            // Carried off introspection so the download can be named before the fetch is made — the
            // explorer has to write a file name at the moment the bytes arrive.
            await Assert.That(links[0].ContentType).IsEqualTo("application/pdf");
        }
    }

    // A hand-built request with no projection at all falls back to the server's default projection,
    // which keys the row by the model's own member names.
    [Test]
    public async Task LinksAQueryWithNoProjection()
    {
        var links = AttachmentLinker.Link(introspection, Request(new WhereOp(new MemberNode(["Name"]))));

        await Assert.That(links[0].KeyColumns).IsEquivalentTo(["id"], CollectionOrdering.Matching);
    }

    // The key is matched by the member it reads rather than by the name it was given, so a renamed
    // projection is still fetchable — and the column named is the one the row actually carries.
    [Test]
    public async Task LinksAProjectionThroughItsAlias()
    {
        var request = Request(
            new SelectOp(new(
            [
                new("Reference", new NodeValue(new MemberNode(["Id"]))),
                new("Title", new NodeValue(new MemberNode(["Name"])))
            ])));

        await Assert.That(AttachmentLinker.Link(introspection, request)[0].KeyColumns).IsEquivalentTo(["reference"], CollectionOrdering.Matching);
    }

    // Nothing identifies the row, so nothing is offered. The alternative is a button whose only
    // possible outcome is a rejection.
    [Test]
    public async Task RefusesAProjectionWithoutTheKey()
    {
        var request = Request(
            new SelectOp(new([new("Title", new NodeValue(new MemberNode(["Name"])))])));

        await Assert.That(AttachmentLinker.Link(introspection, request)).IsEmpty();
    }

    // A key reached through a navigation belongs to that row rather than this one.
    [Test]
    public async Task RefusesAKeyReadThroughANavigation()
    {
        var request = Request(
            new SelectOp(new([new("Id", new NodeValue(new MemberNode(["Manager", "Id"])))])));

        await Assert.That(AttachmentLinker.Link(introspection, request)).IsEmpty();
    }

    /// <summary>
    /// The operators the client refuses to carry an attachment through, for the same reason: each
    /// rewrites what a row is, so a key beside one no longer identifies a row of the source.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Rewriting))]
    public async Task RefusesAnOperatorThatRewritesTheRow(QueryOp op) =>
        await Assert.That(AttachmentLinker.Link(introspection, Request(WholeModel, op))).IsEmpty();

    public static IEnumerable<QueryOp> Rewriting()
    {
        yield return new DistinctOp();
        yield return new SelectManyOp(["Tags"]);
        yield return new JoinOp(
            "Employee",
            JoinKind.Inner,
            new MemberNode(["Id"]),
            new MemberNode(["Id"]),
            InnerPredicate: null,
            [new("Id", JoinSide.Outer, ["Id"])]);
        yield return new SetOp(
            SetKind.Union,
            "Contract",
            Predicate: null,
            new([new("Id", new NodeValue(new MemberNode(["Id"])))]));
        yield return new GroupByOp([new MemberNode(["Name"])]);
    }

    // Narrowing keeps the row and its key, so the attachment stays fetchable.
    [Test]
    public async Task LinksThroughOfType()
    {
        var links = AttachmentLinker.Link(introspection, Request(new OfTypeOp("SealedContract"), WholeModel));

        await Assert.That(links).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LinksAnInheritedAttachment()
    {
        var request = QueryRequest.Create("Sealed", [WholeModel]);

        var links = AttachmentLinker.Link(introspection, request);

        using (Assert.Multiple())
        {
            await Assert.That(links).Count().IsEqualTo(1);
            await Assert.That(links[0].Root).IsEqualTo("Sealed");
            await Assert.That(links[0].Member).IsEqualTo("Document");
            await Assert.That(links[0].KeyColumns).IsEquivalentTo(["id"], CollectionOrdering.Matching);
        }
    }

    // A model with no attachment anywhere is untouched by all of this — no column, no offer.
    [Test]
    public async Task OffersNothingForASourceWithoutAttachments()
    {
        var request = QueryRequest.Create(
            "Employee",
            [new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))]);

        await Assert.That(AttachmentLinker.Link(introspection, request)).IsEmpty();
    }

    [Test]
    public async Task OffersNothingForAnUnknownSource() =>
        await Assert.That(AttachmentLinker.Link(introspection, QueryRequest.Create("Secret", []))).IsEmpty();

    /// <summary>
    /// The parity that matters: the columns are resolved against a request a real translation
    /// produced, through the same synthesized model the explorer's editor compiles against — not
    /// against a hand-built pipeline that only looks like one.
    /// </summary>
    [Test]
    public async Task LinksARequestTheExecutorTranslated()
    {
        var executor = SnippetExecutor.Create(
            introspection,
            [
                MetadataReference.CreateFromFile(typeof(ScryClient).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(QueryRequest).Assembly.Location)
            ]);

        var whole = AttachmentLinker.Link(introspection, executor.Translate("Query.Contract"));
        var projected = AttachmentLinker.Link(
            introspection,
            executor.Translate("Query.Contract.Select(_ => new { Reference = _.Id })"));

        using (Assert.Multiple())
        {
            await Assert.That(whole[0].KeyColumns).IsEquivalentTo(["id"], CollectionOrdering.Matching);
            await Assert.That(projected[0].KeyColumns).IsEquivalentTo(["reference"], CollectionOrdering.Matching);
        }
    }
}
