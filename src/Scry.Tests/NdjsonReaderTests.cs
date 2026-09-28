/// <summary>
/// The client's newline-delimited line reader. It hands back memory pointing into its own buffer, and
/// that buffer slides and grows underneath, so the cases worth pinning are the ones where a line does
/// not sit conveniently inside a single read: split across refills, longer than the buffer, and last
/// with nothing terminating it.
/// </summary>
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
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(Encoding.UTF8.GetString(line.Span));
        }

        return lines;
    }

    [Test]
    public async Task ReadsOneLinePerNewline() =>
        await Assert.That(await ReadAll("one\ntwo\nthree\n")).IsEquivalentTo(["one", "two", "three"], CollectionOrdering.Matching);

    [Test]
    public async Task ReadsALastLineWithNoTerminator() =>
        await Assert.That(await ReadAll("one\ntwo")).IsEquivalentTo(["one", "two"], CollectionOrdering.Matching);

    [Test]
    public async Task StripsACarriageReturnBeforeTheNewline() =>
        await Assert.That(await ReadAll("one\r\ntwo\r\n")).IsEquivalentTo(["one", "two"], CollectionOrdering.Matching);

    [Test]
    public async Task KeepsEmptyLines() =>
        await Assert.That(await ReadAll("one\n\ntwo\n")).IsEquivalentTo(["one", "", "two"], CollectionOrdering.Matching);

    [Test]
    public async Task ReadsNothingFromAnEmptyStream() =>
        await Assert.That(await ReadAll("")).IsEmpty();

    // A byte at a time, so every line is assembled across refills and the buffer slides on each one.
    [Test]
    public async Task ReadsLinesSplitAcrossRefills() =>
        await Assert.That(await ReadAll("alpha\nbeta\ngamma\n", perRead: 1)).IsEquivalentTo(["alpha", "beta", "gamma"], CollectionOrdering.Matching);

    // Past the reader's initial rent, so the buffer has to grow rather than only slide.
    [Test]
    public async Task ReadsALineLongerThanTheBuffer()
    {
        var long1 = new string('a', 40_000);
        var long2 = new string('b', 90_000);

        await Assert.That(await ReadAll($"{long1}\n{long2}\nshort\n", perRead: 4096)).IsEquivalentTo([long1, long2, "short"], CollectionOrdering.Matching);
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

        using (Assert.Multiple())
        {
            await Assert.That(lines).Count().IsEqualTo(4);
            await Assert.That(ScryJson.DeserializeMarker(Encoding.UTF8.GetBytes(lines[0])).Kind).IsEqualTo(ScryStream.Begin);
            await Assert.That(JsonSerializer.Deserialize<JsonElement>(lines[1]).GetProperty("name").GetString()).IsEqualTo("Alice");
        }
    }
}
