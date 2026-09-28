/// <summary>
/// The point at which a buffered response stops being one. What matters is that an envelope small
/// enough to be answered whole still is — declaring its length, with nothing sent early — and that one
/// which is not gives up the length rather than the bytes.
/// </summary>
public class ResponseSpillTests
{
    [Test]
    public async Task DeclaresTheLengthOfAnEnvelopeThatNeverDrained()
    {
        var (context, body) = Context();
        using var spill = new ResponseSpill(context, 1024);
        spill.AllowSpill(true);

        Fill(spill.Output, 100);
        await spill.CompleteAsync();

        using (Assert.Multiple())
        {
            await Assert.That(spill.Committed).IsFalse();
            await Assert.That(context.Response.ContentLength).IsEqualTo(100);
            await Assert.That(context.Response.ContentType).IsEqualTo("application/json");
            await Assert.That(body.Length).IsEqualTo(100);
        }
    }

    // A length can only describe the whole body, and past the first drain the pending bytes are not it.
    [Test]
    public async Task DeclaresNoLengthOnceSomethingHasGoneOut()
    {
        var (context, body) = Context();
        using var spill = new ResponseSpill(context, 100);
        spill.AllowSpill(true);

        Fill(spill.Output, 150);
        await spill.DrainAsync();
        Fill(spill.Output, 50);
        await spill.CompleteAsync();

        using (Assert.Multiple())
        {
            await Assert.That(spill.Committed).IsTrue();
            await Assert.That(context.Response.ContentLength).IsNull();
            await Assert.That(body.Length).IsEqualTo(200);
        }
    }

    [Test]
    public async Task KeepsEveryByteInOrderAcrossSeveralDrains()
    {
        var (context, body) = Context();
        using var spill = new ResponseSpill(context, 64);
        spill.AllowSpill(true);

        var written = new List<byte>();
        for (var chunk = 0; chunk < 40; chunk++)
        {
            var payload = new byte[64];
            Array.Fill(payload, (byte) (chunk % 251));
            payload.CopyTo(spill.Output.GetSpan(payload.Length));
            spill.Output.Advance(payload.Length);
            written.AddRange(payload);
            await spill.DrainAsync();
        }

        await spill.CompleteAsync();

        await Assert.That(body.ToArray()).IsEquivalentTo(written.ToArray(), CollectionOrdering.Matching);
    }

    // Withheld is the default, so a path that never asks keeps behaving as it did before spilling existed.
    [Test]
    public async Task SendsNothingEarlyWithoutPermission()
    {
        var (context, body) = Context();
        using var spill = new ResponseSpill(context, 10);

        Fill(spill.Output, 500);
        await spill.DrainAsync();

        using (Assert.Multiple())
        {
            await Assert.That(spill.Committed).IsFalse();
            await Assert.That(body.Length).IsZero();
            await Assert.That(spill.Pending.Length).IsEqualTo(500);
        }
    }

    [Test]
    public async Task NeverReachesTheThresholdWithoutPermission()
    {
        var (context, _) = Context();
        using var spill = new ResponseSpill(context, 10);

        Fill(spill.Output, 500);

        await Assert.That(spill.ShouldDrain(0)).IsFalse();
    }

    [Test]
    public async Task CountsWhatTheJsonWriterIsStillHoldingTowardsTheThreshold()
    {
        var (context, _) = Context();
        using var spill = new ResponseSpill(context, 100);
        spill.AllowSpill(true);

        Fill(spill.Output, 60);

        using (Assert.Multiple())
        {
            // Sixty in the buffer reaches nothing; sixty more still in the writer reaches the threshold.
            await Assert.That(spill.ShouldDrain(0)).IsFalse();
            await Assert.That(spill.ShouldDrain(40)).IsTrue();
            await Assert.That(spill.ShouldDrain(60)).IsTrue();
        }
    }

    [Test]
    public async Task CommitsOnceHoweverManyTimesItDrains()
    {
        var (context, _) = Context();
        using var spill = new ResponseSpill(context, 8);
        spill.AllowSpill(true);

        Fill(spill.Output, 32);
        await spill.DrainAsync();
        context.Response.ContentType = "changed/by-nobody";
        Fill(spill.Output, 32);
        await spill.DrainAsync();

        // Re-set on the first drain only: past that the headers are the response's own and are fixed.
        await Assert.That(context.Response.ContentType).IsEqualTo("changed/by-nobody");
    }

    [Test]
    public async Task DrainsWhatAJsonWriterFlushedIntoIt()
    {
        var (context, body) = Context();
        using var spill = new ResponseSpill(context, 1);
        spill.AllowSpill(true);

        await using var json = new Utf8JsonWriter(spill.Output);
        json.WriteStartArray();
        for (var item = 0; item < 200; item++)
        {
            json.WriteNumberValue(item);

            // The flush is what puts the writer's bytes in the buffer; draining before it would reset
            // the array out from under the span the writer is still holding.
            await json.FlushAsync();
            await spill.DrainAsync();
        }

        json.WriteEndArray();
        await json.FlushAsync();
        await spill.CompleteAsync();

        await Assert.That(Encoding.UTF8.GetString(body.ToArray())).IsEqualTo($"[{string.Join(',', Enumerable.Range(0, 200))}]");
    }

    static void Fill(IBufferWriter<byte> output, int count)
    {
        output.GetSpan(count)[..count].Fill(1);
        output.Advance(count);
    }

    static (DefaultHttpContext Context, MemoryStream Body) Context()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = body
            }
        };
        return (context, body);
    }
}
