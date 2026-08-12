/// <summary>
/// The client's newline-delimited line reader. It hands back memory pointing into its own buffer, and
/// that buffer slides and grows underneath, so the cases worth pinning are the ones where a line does
/// not sit conveniently inside a single read: split across refills, longer than the buffer, and last
/// with nothing terminating it.
/// </summary>
[TestFixture]
public class NdjsonReaderTests
{
    // Hands out a few bytes per read, so every line of any length crosses at least one refill —
    // which a MemoryStream of the whole body would never exercise.
    sealed class DribbleStream(byte[] content, int perRead) :
        Stream
    {
        int position;

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(Math.Min(perRead, buffer.Length), content.Length - position);
            content.AsSpan(position, take).CopyTo(buffer);
            position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, Cancel cancel = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static async Task<List<string>> ReadAll(string body, int perRead = int.MaxValue)
    {
        using var reader = new NdjsonReader(new DribbleStream(Encoding.UTF8.GetBytes(body), perRead));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(default) is { } line)
        {
            lines.Add(Encoding.UTF8.GetString(line.Span));
        }

        return lines;
    }

    [Test]
    public async Task ReadsOneLinePerNewline() =>
        Assert.That(await ReadAll("one\ntwo\nthree\n"), Is.EqualTo(["one", "two", "three"]));

    [Test]
    public async Task ReadsALastLineWithNoTerminator() =>
        Assert.That(await ReadAll("one\ntwo"), Is.EqualTo(["one", "two"]));

    [Test]
    public async Task StripsACarriageReturnBeforeTheNewline() =>
        Assert.That(await ReadAll("one\r\ntwo\r\n"), Is.EqualTo(["one", "two"]));

    [Test]
    public async Task KeepsEmptyLines() =>
        Assert.That(await ReadAll("one\n\ntwo\n"), Is.EqualTo(["one", "", "two"]));

    [Test]
    public async Task ReadsNothingFromAnEmptyStream() =>
        Assert.That(await ReadAll(""), Is.Empty);

    // A byte at a time, so every line is assembled across refills and the buffer slides on each one.
    [Test]
    public async Task ReadsLinesSplitAcrossRefills() =>
        Assert.That(await ReadAll("alpha\nbeta\ngamma\n", perRead: 1), Is.EqualTo(["alpha", "beta", "gamma"]));

    // Past the reader's initial rent, so the buffer has to grow rather than only slide.
    [Test]
    public async Task ReadsALineLongerThanTheBuffer()
    {
        var long1 = new string('a', 40_000);
        var long2 = new string('b', 90_000);

        Assert.That(await ReadAll($"{long1}\n{long2}\nshort\n", perRead: 4096), Is.EqualTo([long1, long2, "short"]));
    }

    // The rows a stream actually carries, read the way the client reads them.
    [Test]
    public async Task ReadsJsonLinesTheClientCanParse()
    {
        const string body =
            """
            {"$scry":"begin","version":1}
            {"name":"Alice"}
            {"name":"Bob"}
            {"$scry":"end"}

            """;

        var lines = await ReadAll(body.ReplaceLineEndings("\n"), perRead: 7);

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(4));
            Assert.That(ScryJson.DeserializeMarker(Encoding.UTF8.GetBytes(lines[0])).Kind, Is.EqualTo(ScryStream.Begin));
            Assert.That(
                JsonSerializer.Deserialize<JsonElement>(lines[1]).GetProperty("name").GetString(),
                Is.EqualTo("Alice"));
        });
    }
}
