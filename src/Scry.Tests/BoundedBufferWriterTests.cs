/// <summary>
/// What bounds a live query's answer. The bound is on what is committed rather than on the room a
/// writer asks for, it refuses once, and what is accepted before the refusal stays readable.
/// </summary>
public class BoundedBufferWriterTests
{
    // A JSON writer asks for three bytes per character of a string it may have to transcode, and never
    // for less than a few kilobytes. Refusing on the ask would turn this value away at a third of the
    // limit.
    [Test]
    public async Task AValueWellInsideTheLimitIsNotRefusedForTheRoomItAskedFor()
    {
        using var output = new BoundedBufferWriter(1500);

        await using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStringValue(new string('x', 1000));
        }

        await Assert.That(output.WrittenMemory.Length).IsEqualTo(1002);
    }

    // An answer within a few kilobytes of the limit, where the writer's smallest ask alone would cross
    // it.
    [Test]
    public async Task AnAnswerJustUnderTheLimitIsAccepted()
    {
        using var output = new BoundedBufferWriter(100);

        await using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStringValue(new string('x', 90));
        }

        await Assert.That(output.WrittenMemory.Length).IsEqualTo(92);
    }

    [Test]
    public async Task AValuePastTheLimitIsRefused()
    {
        using var output = new BoundedBufferWriter(100);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() =>
        {
            using var json = new Utf8JsonWriter(output);
            json.WriteStringValue(new string('x', 200));
        });

        await Assert.That(exception.Message).Contains("100 bytes");
    }

    [Test]
    public async Task TheLimitItselfIsAccepted()
    {
        using var output = new BoundedBufferWriter(10);

        Fill(output, 10);

        using (Assert.Multiple())
        {
            await Assert.That(output.WrittenMemory.Length).IsEqualTo(10);
            Assert.ThrowsExactly<ScryValidationException>(() => Fill(output, 1));
        }
    }

    // The writer flushes what it was holding as it is disposed. A second refusal from there would
    // replace the first, so what is committed after the refusal is dropped rather than refused again —
    // and never lands in the buffer behind it.
    [Test]
    public async Task RefusesOnceThenDropsWhatIsCommitted()
    {
        using var output = new BoundedBufferWriter(10);

        Fill(output, 8);
        Assert.ThrowsExactly<ScryValidationException>(() => Fill(output, 8));

        await Assert.That(() => Fill(output, 8)).ThrowsNothing();
        await Assert.That(output.WrittenMemory.Length).IsEqualTo(8);
    }

    static void Fill(BoundedBufferWriter output, int count)
    {
        output.GetSpan(count)[..count].Fill(1);
        output.Advance(count);
    }
}
