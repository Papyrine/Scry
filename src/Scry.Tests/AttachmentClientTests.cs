/// <summary>
/// The client half of the claim check: what the translator sends, what it refuses, and the handles a
/// materialized row comes back carrying. Fetching through one is HTTP-only and is covered by the
/// integration tests; what is asserted here is that the handle knows what to fetch.
/// </summary>
[TestFixture]
public class AttachmentClientTests
{
    /// <summary>
    /// Stands in for the generated model, which this assembly has no generator run to produce. Written
    /// exactly as the generator would emit it — the attachment is absent from the [ScryModel] member
    /// list, and the key it is fetched by is named there instead.
    /// </summary>
    [ScryModel("Contract", "Id", "Name", Keys = ["Id"], Attachments = ["Document"])]
    class ContractModel
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public ScryAttachment Document { get; init; } = null!;
    }

    // ReSharper disable NotAccessedPositionalProperty.Local
    record ContractRow(int Id, ScryAttachment Document);

    record NamedRow(string Name);
    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public Task ProjectionOmitsTheAttachment()
    {
        using var context = TestContext.CreateSeeded();

        // The attachment leaves the wire projection entirely: the server is asked for the key, and the
        // handle is built from it on the way back.
        var request = ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
            .Select(_ => new ContractRow(_.Id, _.Document))
            .ToScryRequest();

        return Verify(request);
    }

    [Test]
    public async Task ProjectionCarriesTheHandle()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = await ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
            .Where(_ => _.Id == 1)
            .Select(_ => new ContractRow(_.Id, _.Document))
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].Document, Is.Not.Null);
            Assert.That(rows[0].Document.Source, Is.EqualTo("Contract"));
            Assert.That(rows[0].Document.Member, Is.EqualTo("Document"));
        });
    }

    // No Select at all: every member the model declares comes back, so the key is already there and
    // the handle hangs off the row itself.
    [Test]
    public async Task WholeModelCarriesTheHandle()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = await ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
            .OrderBy(_ => _.Id)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(rows.Select(_ => _.Name), Is.EqualTo(new[] {"Lease", "Draft", "Sealed"}));
            Assert.That(rows.All(_ => _.Document is not null), Is.True);
        });
    }

    // Streaming binds each row as it arrives — there is no materialized list to walk afterwards — so
    // the handle is attached on a path of its own.
    [Test]
    public async Task StreamedRowsCarryTheHandle()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = new List<ContractModel>();
        var query = StreamingClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
            .OrderBy(_ => _.Id);
        await foreach (var row in query.ToAsyncEnumerable())
        {
            rows.Add(row);
        }

        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(_ => _.Name), Is.EqualTo(new[] {"Lease", "Draft", "Sealed"}));
            Assert.That(rows.All(_ => _.Document is not null), Is.True);
            Assert.That(rows[0].Document.Member, Is.EqualTo("Document"));
        });
    }

    // A projected row has no setter to fill, so taking the handle rebuilds it through its
    // constructor. Streaming does that per row rather than over a list.
    [Test]
    public async Task StreamedProjectionCarriesTheHandle()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = new List<ContractRow>();
        var query = StreamingClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
            .Where(_ => _.Id == 1)
            .Select(_ => new ContractRow(_.Id, _.Document));
        await foreach (var row in query.ToAsyncEnumerable())
        {
            rows.Add(row);
        }

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].Id, Is.EqualTo(1));
            Assert.That(rows[0].Document.Source, Is.EqualTo("Contract"));
            Assert.That(rows[0].Document.Member, Is.EqualTo("Document"));
        });
    }

    [Test]
    public async Task SingleRowCarriesTheHandle()
    {
        await using var context = TestContext.CreateSeeded();

        var row = await ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
            .FirstAsync(_ => _.Id == 2);

        Assert.That(row!.Document, Is.Not.Null);
    }

    // A client whose transport cannot fetch says so when the handle is opened, rather than at
    // materialization: the row is still perfectly readable, and only this one operation is not.
    [Test]
    public async Task OpeningWithoutAnAttachmentTransportIsRefused()
    {
        await using var context = TestContext.CreateSeeded();

        var row = await ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
            .FirstAsync(_ => _.Id == 1);

        var exception = Assert.ThrowsAsync<NotSupportedException>(() => row!.Document.OpenAsync());
        Assert.That(exception!.Message, Does.Contain("does not fetch attachments"));
    }

    [Test]
    public void ProjectingAnAttachmentWithoutItsKeyIsRefused()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<NotSupportedException>(
            () => ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
                .Select(_ => new {_.Name, _.Document})
                .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("_.Id"));
    }

    [Test]
    public void FilteringOnAnAttachmentIsRefused()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<NotSupportedException>(
            () => ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
                .Where(_ => _.Document != null)
                .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("is not a value"));
    }

    [Test]
    public void OrderingByAnAttachmentIsRefused()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<NotSupportedException>(
            () => ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
                .OrderBy(_ => _.Document)
                .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("is not a value"));
    }

    // Distinct rewrites what a row is, so a key projected beside an attachment no longer identifies
    // one row of the source.
    [Test]
    public void DistinctCarryingAnAttachmentIsRefused()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<NotSupportedException>(
            () => ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
                .Select(_ => new ContractRow(_.Id, _.Document))
                .Distinct()
                .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("cannot be carried through Distinct"));
    }

    [Test]
    public void GroupingByAnAttachmentIsRefused()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<NotSupportedException>(
            () => ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
                .GroupBy(_ => _.Document)
                .Select(_ => new {Count = _.Count()})
                .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("is not a value"));
    }

    // A projection reading nothing but the attachment has no members left to send once it is taken
    // out. Reported as the missing key it really is, rather than as an empty projection.
    [Test]
    public void ProjectingOnlyAnAttachmentIsRefused()
    {
        using var context = TestContext.CreateSeeded();

        var exception = Assert.Throws<NotSupportedException>(
            () => ClientFor(context).Source<ContractModel>("Contract", ["Id", "Name"])
                .Select(_ => new {_.Document})
                .ToScryRequest());

        Assert.That(exception!.Message, Does.Contain("Project the row's key beside the attachment"));
    }

    // A query over a model with no attachment is untouched by any of this — the same request, and no
    // handles to bind.
    [Test]
    public async Task ModelWithoutAttachmentsIsUnaffected()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = await ClientFor(context).Source<Employee>("Employee")
            .Where(_ => _.Name == "Alice")
            .Select(_ => new NamedRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Single().Name, Is.EqualTo("Alice"));
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));

    // The same processor answered row by row instead of as one response, which is all
    // ToAsyncEnumerable needs of a transport.
    static ScryClient StreamingClientFor(TestContext context) =>
        new(
            (request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)),
            (request, _) => Stream(request, context));

    static async IAsyncEnumerable<JsonElement> Stream(QueryRequest request, TestContext context)
    {
        var (_, rows) = SharedProcessor.Instance.Stream(request, context);
        await foreach (var row in rows)
        {
            // Each row is serialized exactly as a response payload's rows are, so what the client
            // reads here is the shape the HTTP stream would hand it.
            yield return JsonSerializer.SerializeToElement(row, ScryJson.Options);
        }
    }
}
