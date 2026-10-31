using Microsoft.EntityFrameworkCore;

/// <summary>
/// A batch on a model with no <c>[BinaryTransfer]</c> member drains between entries once its envelope
/// passes the spill threshold, and from then on its headers are on the wire. These pin what the
/// entries after that point still owe the response, against a body that refuses a header write once
/// it has been written to, as a server's does.
/// </summary>
/// <remarks>
/// The model is compiled for the test: this assembly's own carries a binary member, and a batch over
/// it never drains.
/// </remarks>
public class BatchSpillHeaderTests
{
    static (ScryProcessor Processor, DbContext Data) Start()
    {
        var (assembly, _) = CompiledModel.Compile(
            """
            public sealed class SpillContext(DbContextOptions options) : DbContext(options);

            [QueryablePoco]
            public class Note
            {
                public int Id { get; set; }
                public string Body { get; set; } = "";
                [Sensitive]
                public string Secret { get; set; } = "";

                public static IEnumerable<Note> Rows() =>
                    Enumerable.Range(0, 100).Select(_ => new Note { Id = _, Body = new string('w', 200), Secret = "s" });
            }
            """);
        var contextType = assembly.GetType("SpillContext")!;
        var noteType = assembly.GetType("Note")!;
        var rows = (System.Collections.IEnumerable) noteType.GetMethod("Rows")!.Invoke(null, null)!;

        var options = new ScryOptions(contextType);
        options.PocoSources[noteType] = _ => Queryable.AsQueryable(rows);
        var processor = new ScryProcessor(Schema.Build(options), options);

        // Never opened: every query here reads the poco source.
        var contextOptions = SqlServerDbContextOptionsExtensions
            .UseSqlServer(new DbContextOptionsBuilder(), "Server=(localdb)\\ScryUnused;Database=ScryUnused")
            .Options;
        var data = (DbContext) Activator.CreateInstance(contextType, contextOptions)!;
        return (processor, data);
    }

    static QueryRequest Projecting(string member) =>
        QueryRequest.Create("Note", [new SelectOp(new([new(member, new NodeValue(new MemberNode([member])))]))]);

    // The first entry alone is past the threshold, so the batch drains after it, and the second
    // projects a sensitive member: the response it is part of must not be kept, and the write that
    // says so can no longer be made where the entry is answered.
    [Test]
    public async Task AnEntryAfterTheBatchDrainsMarksTheResponseBeforeItLeaves()
    {
        var (processor, data) = Start();
        await using var _ = data;
        var body = new StartingBody();
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = body
            }
        };
        body.Headers = (HeaderDictionary) context.Response.Headers;

        using var spill = new ResponseSpill(context, threshold: 4096);
        await processor.ExecuteBatchBufferedAsync(
            QueryBatchRequest.Create([Projecting("Body"), Projecting("Secret")]),
            data,
            EmptyServiceProvider.Instance,
            context.Request.Headers,
            context.Response.Headers,
            spill.Output,
            binary: null,
            spill);
        await spill.CompleteAsync();

        var sent = Encoding.UTF8.GetString(body.ToArray());
        using (Assert.Multiple())
        {
            await Assert.That(spill.Committed).IsTrue().Because("the batch must actually drain");
            await Assert.That(sent).DoesNotContain("\"error\"");
            await Assert.That(sent).Contains("\"secret\":\"s\"");
            await Assert.That(context.Response.Headers.CacheControl.ToString()).IsEqualTo("no-store");
        }
    }

    // Nothing still to come needs the header, so the batch is not marked with it: what is decided ahead
    // of the entries is no wider than what they would have written themselves.
    [Test]
    public async Task ABatchWhoseLaterEntriesNeedNothingIsLeftUnmarked()
    {
        var (processor, data) = Start();
        await using var _ = data;
        var body = new StartingBody();
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = body
            }
        };
        body.Headers = (HeaderDictionary) context.Response.Headers;

        using var spill = new ResponseSpill(context, threshold: 4096);
        await processor.ExecuteBatchBufferedAsync(
            QueryBatchRequest.Create([Projecting("Body"), Projecting("Id")]),
            data,
            EmptyServiceProvider.Instance,
            context.Request.Headers,
            context.Response.Headers,
            spill.Output,
            binary: null,
            spill);
        await spill.CompleteAsync();

        using (Assert.Multiple())
        {
            await Assert.That(spill.Committed).IsTrue().Because("the batch must actually drain");
            await Assert.That(Encoding.UTF8.GetString(body.ToArray())).DoesNotContain("\"error\"");
            await Assert.That(context.Response.Headers.CacheControl.Count).IsEqualTo(0);
        }
    }

    // A response body that, like a server's, makes the headers read-only once anything is written.
    sealed class StartingBody :
        MemoryStream
    {
        public HeaderDictionary? Headers { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Start();
            base.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, Cancel cancel = default)
        {
            Start();
            return base.WriteAsync(buffer, cancel);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, Cancel cancel)
        {
            Start();
            return base.WriteAsync(buffer, offset, count, cancel);
        }

        void Start() =>
            Headers!.IsReadOnly = true;
    }
}
