/// <summary>
/// The sidecar's server-sent-events framer. It reads a copy of a live query's body as the consumer
/// pulls it, so it sees the stream cut at whatever boundaries the network chose — and it must agree
/// with the platform's own parser about what counts as an event, or the panel's counts would
/// disagree with the answers the app acted on.
/// </summary>
public class SseFramerTests
{
    public record Framed(string Name, string? Id, string Data, bool Truncated, int Bytes);

    // The one that matters most: the same event, delivered in two pieces cut at every position there
    // is, is one event every time.
    [Test]
    public async Task EventsAreFramedAcrossEveryChunkBoundary()
    {
        var whole = "event: result\nid: a3f1\ndata: {\"a\":1}\n\n";

        for (var split = 1; split < whole.Length; split++)
        {
            var frames = Frame(4096, whole[..split], whole[split..]);

            await Assert.That(frames).Count().IsEqualTo(1).Because($"split at {split}");
            await Assert.That(frames[0].Name).IsEqualTo(ScryLive.Result).Because($"split at {split}");
            await Assert.That(frames[0].Id).IsEqualTo("a3f1").Because($"split at {split}");
            await Assert.That(frames[0].Data).IsEqualTo("{\"a\":1}").Because($"split at {split}");
            await Assert.That(frames[0].Bytes).IsEqualTo(whole.Length).Because($"split at {split}");
        }
    }

    // The format says an event with an empty data buffer is not dispatched; .NET's parser dispatches
    // on a data line having been seen, empty or not. This server always writes one, which is why the
    // client sees the two events that carry nothing — so this follows .NET, not the format.
    [Test]
    public async Task AnEventIsOnlyFramedWhereADataLineWasSeen()
    {
        await Assert.That(Frame(4096, "event: ping\n\n")).IsEmpty();

        var frames = Frame(4096, "event: ping\ndata: \n\n");
        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].Name).IsEqualTo(ScryLive.Ping);
        await Assert.That(frames[0].Data).IsEmpty();
    }

    [Test]
    public async Task LinesEndWithAnyOfTheThreeEndings()
    {
        foreach (var ending in (string[]) ["\n", "\r\n", "\r"])
        {
            var text = $"event: ping{ending}data: x{ending}{ending}";
            var frames = Frame(4096, text);

            await Assert.That(frames).Count().IsEqualTo(1).Because(ending);
            await Assert.That(frames[0].Data).IsEqualTo("x").Because(ending);
            await Assert.That(frames[0].Bytes).IsEqualTo(text.Length).Because(ending);
        }
    }

    // A carriage return ending one read and its newline beginning the next is one line ending, not
    // two — and two would frame an event early.
    [Test]
    public async Task ACarriageReturnSplitFromItsNewlineIsOneEnding()
    {
        var frames = Frame(4096, "event: ping\r", "\ndata: x\r\n\r\n");

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].Name).IsEqualTo(ScryLive.Ping);
        await Assert.That(frames[0].Data).IsEqualTo("x");
    }

    [Test]
    public async Task CommentsAndUnknownFieldsAreIgnoredButCounted()
    {
        var text = ": keep alive\nevent: ping\nretry: 5000\ndata: x\n\n";
        var frames = Frame(4096, text);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].Name).IsEqualTo(ScryLive.Ping);
        await Assert.That(frames[0].Data).IsEqualTo("x");
        await Assert.That(frames[0].Bytes).IsEqualTo(text.Length);
    }

    [Test]
    public async Task SeveralDataLinesAreJoinedByTheNewlinesThatSeparatedThem()
    {
        var frames = Frame(4096, "event: result\ndata: one\ndata: two\n\n");

        await Assert.That(frames[0].Data).IsEqualTo("one\ntwo");
    }

    // An answer longer than the panel keeps is listed by its size rather than shown in part, and the
    // size stays exact — it is counted off the chunks, not off what was stored.
    [Test]
    public async Task DataBeyondTheCapIsCutAndFlagged()
    {
        var payload = new string('x', 5000);
        var text = $"event: result\ndata: {payload}\n\n";
        var frames = Frame(64, text);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].Truncated).IsTrue();
        await Assert.That(frames[0].Data.Length).IsLessThanOrEqualTo(64);
        await Assert.That(frames[0].Bytes).IsEqualTo(text.Length);
    }

    [Test]
    public async Task AByteOrderMarkLeadsTheStreamRatherThanEveryLine()
    {
        var frames = Frame(4096, "﻿event: ping\ndata: x\n\n");

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].Name).IsEqualTo(ScryLive.Ping);
    }

    // An event a newer server sends and this client has no name for is still worth counting: "why is
    // my client ignoring this?" is exactly what the panel is opened to answer.
    [Test]
    public async Task AnEventThisClientHasNoNameForIsStillFramed()
    {
        var frames = Frame(4096, "event: rebalance\ndata: {}\n\n");

        await Assert.That(frames[0].Name).IsEqualTo("rebalance");
    }

    [Test]
    public async Task AnEventThatNamesNothingIsTheDefaultOne()
    {
        var frames = Frame(4096, "data: {}\n\n");

        await Assert.That(frames[0].Name).IsEqualTo("message");
    }

    // The framer hands out its own buffer, so what a frame carries is copied out before the next one
    // overwrites it.
    static List<Framed> Frame(int maxDataBytes, params string[] chunks)
    {
        using var framer = new SseFramer(maxDataBytes);
        var frames = new List<Framed>();
        foreach (var chunk in chunks)
        {
            framer.Append(
                Encoding.UTF8.GetBytes(chunk),
                _ => frames.Add(new(_.Name, _.Id, Encoding.UTF8.GetString(_.Data.Span), _.Truncated, _.Bytes)));
        }

        return frames;
    }
}
