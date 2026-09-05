/// <summary>
/// How a buffered response reaches the array the client keeps. HttpClient's own buffering copied
/// every body twice on the way; now the body is read headers-first into an array sized from the
/// length the server declared, and a declared length is a claim the read checks rather than trusts.
/// </summary>
[TestFixture]
public class BufferedReadTests
{
    [Test]
    public async Task ReadsABodyThatDeclaresItsLength()
    {
        var client = Stubbed(_ => Scalar(7));

        Assert.That(await client.Source<NameOnly>("Employee", ["Name"]).CountAsync(), Is.EqualTo(7));
    }

    // A chunked body declares nothing, so the array cannot be sized ahead: the buffer grows with what
    // arrives instead, and the result is the same.
    [Test]
    public async Task ReadsABodyThatDeclaresNoLength()
    {
        long? declared = null;
        var client = Stubbed(
            _ =>
            {
                var content = new StreamContent(new Unseekable(Utf8(Scalar(7).Content)));
                declared = content.Headers.ContentLength;
                return new(HttpStatusCode.OK)
                {
                    Content = content
                };
            });

        Assert.That(await client.Source<NameOnly>("Employee", ["Name"]).CountAsync(), Is.EqualTo(7));
        Assert.That(declared, Is.Null);
    }

    // The declared length sizes the array and nothing else: a body that ends short of it is a wire
    // failure that names the shortfall, rather than a payload padded out with zeros.
    [Test]
    public void ReportsABodyShorterThanItsDeclaredLength()
    {
        var length = 0;
        var client = Stubbed(
            _ =>
            {
                var bytes = Utf8(Scalar(7).Content);
                length = bytes.Length;
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentLength = bytes.Length + 10;
                return new(HttpStatusCode.OK)
                {
                    Content = content
                };
            });

        var exception = Assert.ThrowsAsync<ScryWireException>(
            () => client.Source<NameOnly>("Employee", ["Name"]).CountAsync());

        Assert.That(exception!.Message, Does.Contain($"{length} of the {length + 10} bytes"));
    }

    // A binary part declares its length, so it is read straight into an array of that size.
    [Test]
    public async Task ReadsAPartThatDeclaresItsLength()
    {
        var client = Stubbed(_ => Multipart(Part("ABC", declared: 3)));

        var rows = await client.Source<NameAndAvatar>("Employee", ["Name", "Avatar"]).ToListAsync();

        Assert.That(rows.Single().Avatar, Is.EqualTo("ABC"u8.ToArray()));
    }

    // A part without one is read the growing way, to the same result.
    [Test]
    public async Task ReadsAPartThatDeclaresNoLength()
    {
        var client = Stubbed(_ => Multipart(Part("ABC", declared: null)));

        var rows = await client.Source<NameAndAvatar>("Employee", ["Name", "Avatar"]).ToListAsync();

        Assert.That(rows.Single().Avatar, Is.EqualTo("ABC"u8.ToArray()));
    }

    // A part's declared length is checked against the part on both sides: one that ends short would
    // otherwise be padded, and one that runs long would have its tail dropped on the way to the next
    // section.
    [TestCase(5, "ended after 3 of the 5 bytes")]
    [TestCase(2, "more than the 2 bytes")]
    public void ReportsAPartThatDisagreesWithItsDeclaredLength(int declared, string expected)
    {
        var client = Stubbed(_ => Multipart(Part("ABC", declared)));

        var exception = Assert.ThrowsAsync<ScryWireException>(
            () => client.Source<NameAndAvatar>("Employee", ["Name", "Avatar"]).ToListAsync());

        Assert.That(exception!.Message, Does.Contain(expected));
    }

    static byte[] Utf8(HttpContent content) =>
        content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

    static string Part(string body, int? declared) =>
        $"Content-Type: {ScryBinary.PartContentType}\r\n" +
        (declared is { } length ? $"Content-Length: {length}\r\n" : "") +
        $"\r\n{body}\r\n";

    static HttpResponseMessage Multipart(string part)
    {
        const string boundary = "scrytest";
        var envelope = ScryJson.Serialize(
            QueryResponse.Create(
                ResultKind.List,
                JsonDocument.Parse("""[{"name":"Al","avatar":{"$bin":0}}]""").RootElement.Clone()));
        var bytes = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n{part}" +
            $"--{boundary}\r\nContent-Type: application/json\r\n\r\n{envelope}\r\n" +
            $"--{boundary}--\r\n");
        var content = new ByteArrayContent(bytes);
        content.Headers.TryAddWithoutValidation("Content-Type", $"{ScryBinary.ContentType}; boundary={boundary}");
        return new(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    public class NameOnly
    {
        public string Name { get; set; } = "";
    }

    public class NameAndAvatar
    {
        public string Name { get; set; } = "";

        public byte[]? Avatar { get; set; }
    }

    // A stream that cannot say how long it is, which is what a chunked response reads as.
    sealed class Unseekable(byte[] bytes) :
        Stream
    {
        readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    static ScryClient Stubbed(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new("http://localhost")
        };

        return ScryClient.ForHttp(http, "/api/query");
    }

    static HttpResponseMessage Scalar(int value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                ScryJson.Serialize(
                    QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(value))))
        };

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel) =>
            Task.FromResult(respond(request));
    }
}
