/// <summary>
/// The buffer a buffered response is written into. It hands out spans of a rented array and swaps
/// that array as it grows, so what matters is that everything written survives the swaps in order and
/// that nothing past what was written is ever visible.
/// </summary>
[TestFixture]
public class PooledBufferWriterTests
{
    [Test]
    public void KeepsWhatWasWrittenAcrossGrowth()
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

        Assert.Multiple(() =>
        {
            Assert.That(writer.WrittenCount, Is.EqualTo(written.Count));
            Assert.That(writer.WrittenMemory.ToArray(), Is.EqualTo(written.ToArray()));
        });
    }

    [Test]
    public void HonoursASizeHintLargerThanTheCurrentBuffer()
    {
        using var writer = new PooledBufferWriter();

        var span = writer.GetSpan(1024 * 1024);

        Assert.That(span.Length, Is.GreaterThanOrEqualTo(1024 * 1024));
    }

    [Test]
    public void ExposesNothingBeyondWhatWasWritten()
    {
        using var writer = new PooledBufferWriter();

        "abc"u8.CopyTo(writer.GetSpan(3));
        writer.Advance(3);

        Assert.Multiple(() =>
        {
            Assert.That(writer.WrittenCount, Is.EqualTo(3));
            Assert.That(writer.WrittenMemory.Length, Is.EqualTo(3));
            Assert.That(Encoding.UTF8.GetString(writer.WrittenMemory.Span), Is.EqualTo("abc"));
        });
    }

    [Test]
    public void ResetKeepsTheArrayAndDropsTheContent()
    {
        using var writer = new PooledBufferWriter();

        "first"u8.CopyTo(writer.GetSpan(5));
        writer.Advance(5);
        writer.Reset();
        "second"u8.CopyTo(writer.GetSpan(6));
        writer.Advance(6);

        Assert.That(Encoding.UTF8.GetString(writer.WrittenMemory.Span), Is.EqualTo("second"));
    }

    [Test]
    public void WritesTheSameBytesAsTheFrameworksOwnWriter()
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

        Assert.That(pooled.WrittenMemory.ToArray(), Is.EqualTo(expected.WrittenMemory.ToArray()));
    }

    [Test]
    public void RefusesUseAfterDisposal()
    {
        var writer = new PooledBufferWriter();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
    }

    // Returned once, not once per call — a double return would hand the same array to two renters.
    [Test]
    public void ToleratesBeingDisposedTwice()
    {
        var writer = new PooledBufferWriter();
        writer.Dispose();

        Assert.DoesNotThrow(writer.Dispose);
    }
}
