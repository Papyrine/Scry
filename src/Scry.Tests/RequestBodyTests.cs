/// <summary>
/// How the endpoints read a request body. A declared Content-Length is a hint for sizing the buffer
/// and nothing more: the host enforces its body limit only once the body is read, so what this
/// allocates before that first read has to be bounded by something other than the client's claim.
/// </summary>
[TestFixture]
public class RequestBodyTests
{
    [Test]
    public async Task ReadsADeclaredBodyWhole()
    {
        var body = Bytes(1000);
        var context = Context(body, declared: body.Length);

        var read = await ScryServiceExtensions.ReadBody(context);

        Assert.That(read, Is.EqualTo(body));
    }

    [Test]
    public async Task ReadsAnUndeclaredBodyWhole()
    {
        var body = Bytes(1000);
        var context = Context(body, declared: null);

        var read = await ScryServiceExtensions.ReadBody(context);

        Assert.That(read, Is.EqualTo(body));
    }

    [Test]
    public async Task ReadsWhatArrivedWhenTheBodyIsShorterThanDeclared()
    {
        var body = Bytes(10);
        var context = Context(body, declared: 100);

        var read = await ScryServiceExtensions.ReadBody(context);

        Assert.That(read, Is.EqualTo(body));
    }

    [Test]
    public async Task ReadsABodyPastTheCeilingWhole()
    {
        var body = Bytes(ScryServiceExtensions.PresizeCeiling * 3 + 7);
        var context = Context(body, declared: body.Length);

        var read = await ScryServiceExtensions.ReadBody(context);

        Assert.That(read, Is.EqualTo(body));
    }

    [Test]
    public async Task DoesNotSizeToALengthTheClientOnlyClaimed()
    {
        // Declares far more than it sends. Sizing the buffer to the claim would commit that much memory
        // per connection before the host's own limit is ever consulted.
        var body = Bytes(10);
        var context = Context(body, declared: int.MaxValue - 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = await ScryServiceExtensions.ReadBody(context);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Multiple(() =>
        {
            Assert.That(read, Is.EqualTo(body));
            Assert.That(allocated, Is.LessThan(1024 * 1024));
        });
    }

    static byte[] Bytes(int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[i] = (byte) (i % 251);
        }

        return bytes;
    }

    // Every read completes synchronously off a MemoryStream, so the whole read runs on the calling
    // thread and its allocations are the calling thread's to measure.
    static DefaultHttpContext Context(byte[] body, long? declared) =>
        new()
        {
            Request =
            {
                Body = new MemoryStream(body),
                ContentLength = declared
            }
        };
}
