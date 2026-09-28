/// <summary>
/// The drift event is promised at most once per client. A scoped client awaiting several queries at
/// once records the server's stamp from several threads, and a check-then-set raise could fire twice.
/// </summary>
public class SchemaDriftEventTests
{
    [Test]
    public async Task RaisedOnceUnderConcurrentResponses()
    {
        var client = new ScryClient(async (_, _) =>
        {
            await Task.Yield();
            return QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(1)) with
            {
                Stamp = "server"
            };
        })
        {
            SchemaStamp = "client"
        };
        var raised = 0;
        client.SchemaStaleDetected += _ => Interlocked.Increment(ref raised);

        await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(_ => client.Source<NameOnly>("Employee", ["Name"]).CountAsync()));

        using (Assert.Multiple())
        {
            await Assert.That(raised).IsEqualTo(1);
            await Assert.That(client.SchemaStale).IsTrue();
        }
    }

    public class NameOnly
    {
        public string Name { get; set; } = "";
    }
}
