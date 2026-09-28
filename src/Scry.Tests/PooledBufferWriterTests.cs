/// <summary>
/// The buffer a buffered response is written into. It hands out spans of a rented array and swaps
/// that array as it grows, so what matters is that everything written survives the swaps in order and
/// that nothing past what was written is ever visible.
/// </summary>
public class PooledBufferWriterTests
{
    [Test]
    public async Task KeepsWhatWasWrittenAcrossGrowth()
    {
        using var writer = new PooledBufferWriter();

        // Past the initial rent, so the array is replaced at least twice on the way.
        var written = new List<byte>();
        for (var chunk = 0; chunk < 400; chunk++)
        {
            var payload = new byte[256];
            Array.Fill(payload, (byte) (chunk % 251));
            payload.CopyTo(writer.GetSpan(payload.Length));
            writer.Advance(payload.Length);
            written.AddRange(payload);
        }

        using (Assert.Multiple())
        {
            await Assert.That(writer.WrittenCount).IsEqualTo(written.Count);
            await Assert.That(writer.WrittenMemory.ToArray()).IsEquivalentTo(written.ToArray(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task HonoursASizeHintLargerThanTheCurrentBuffer()
    {
        using var writer = new PooledBufferWriter();

        var span = writer.GetSpan(1024 * 1024);

        await Assert.That(span.Length).IsGreaterThanOrEqualTo(1024 * 1024);
    }

    [Test]
    public async Task ExposesNothingBeyondWhatWasWritten()
    {
        using var writer = new PooledBufferWriter();

        "abc"u8.CopyTo(writer.GetSpan(3));
        writer.Advance(3);

        using (Assert.Multiple())
        {
            await Assert.That(writer.WrittenCount).IsEqualTo(3);
            await Assert.That(writer.WrittenMemory.Length).IsEqualTo(3);
            await Assert.That(Encoding.UTF8.GetString(writer.WrittenMemory.Span)).IsEqualTo("abc");
        }
    }

    [Test]
    public async Task ResetKeepsTheArrayAndDropsTheContent()
    {
        using var writer = new PooledBufferWriter();

        "first"u8.CopyTo(writer.GetSpan(5));
        writer.Advance(5);
        writer.Reset();
        "second"u8.CopyTo(writer.GetSpan(6));
        writer.Advance(6);

        await Assert.That(Encoding.UTF8.GetString(writer.WrittenMemory.Span)).IsEqualTo("second");
    }

    [Test]
    public async Task WritesTheSameBytesAsTheFrameworksOwnWriter()
    {
        var expected = new ArrayBufferWriter<byte>();
        using var pooled = new PooledBufferWriter();

        foreach (var writer in new IBufferWriter<byte>[] {expected, pooled})
        {
            using var json = new Utf8JsonWriter(writer);
            json.WriteStartObject();
            json.WriteString("name", "Alice");
            json.WriteNumber("rank", 1);
            json.WriteEndObject();
            json.Flush();
        }

        await Assert.That(pooled.WrittenMemory.ToArray()).IsEquivalentTo(expected.WrittenMemory.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public void RefusesUseAfterDisposal()
    {
        var writer = new PooledBufferWriter();
        writer.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.GetSpan(1));
    }

    // Returned once, not once per call — a double return would hand the same array to two renters.
    [Test]
    public async Task ToleratesBeingDisposedTwice()
    {
        var writer = new PooledBufferWriter();
        writer.Dispose();

        await Assert.That(writer.Dispose).ThrowsNothing();
    }
}
