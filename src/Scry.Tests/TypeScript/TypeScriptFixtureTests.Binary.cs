using Microsoft.AspNetCore.TestHost;

public partial class TypeScriptFixtureTests
{
    /// <summary>
    /// What a client fetching an attachment is answered, and a batch whose entries carry binary parts:
    /// the bytes of an attachment, the empty answer for one holding nothing, the refusal for one the
    /// check denies, a request with no key, and one flat multipart for a whole batch, its parts
    /// numbered across every entry and its envelope last.
    /// </summary>
    /// <remarks>
    /// An attachment's bytes are kept as base64, as a multipart body is, and with the advisory length
    /// the server declares beside them.
    /// </remarks>
    [Test]
    public async Task BinaryFramings()
    {
        var app = await Host();
        try
        {
            using var http = app.GetTestClient();

            var binary = Capture(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Id).Take(2).Select(_ => new {_.Name, _.Avatar}).ToListAsync());
            var list = Capture(_ => _.Source<Employee>("Employee").Where(_ => _.Active).OrderBy(_ => _.Name).Select(_ => new {_.Name}).ToListAsync());
            var batch = QueryBatchRequest.Create([binary, list, binary]);

            Exchange[] exchanges =
            [
                await Send(http, "An attachment", HttpMethod.Post, "/api/query/attachment", Attachment(new AttachmentKey("1", ClrTypeTag.Int32)), "Content-Length", "X-Content-Type-Options"),
                await Send(http, "An attachment holding nothing", HttpMethod.Post, "/api/query/attachment", Attachment(new AttachmentKey("2", ClrTypeTag.Int32))),
                await Send(http, "An attachment the check refuses", HttpMethod.Post, "/api/query/attachment", Attachment(new AttachmentKey("3", ClrTypeTag.Int32))),
                await Send(http, "An attachment asked for with no key", HttpMethod.Post, "/api/query/attachment", Attachment()),
                await Send(http, "A batch carrying binary parts", HttpMethod.Post, "/api/query/batch", ScryJson.Serialize(batch))
            ];

            var json = Document(writer =>
            {
                writer.WriteStartArray();
                foreach (var exchange in exchanges)
                {
                    exchange.Write(writer);
                }

                writer.WriteEndArray();
            });

            await Verify(json, "json")
                .UseFileName("binary")
                .NotInline();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    static string Attachment(params AttachmentKey[] keys) =>
        Encoding.UTF8.GetString(ScryJson.SerializeToUtf8(AttachmentRequest.Create("Contract", "Document", keys)));
}
