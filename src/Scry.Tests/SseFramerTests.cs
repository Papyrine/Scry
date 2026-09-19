/// <summary>
/// The sidecar's server-sent-events framer. It reads a copy of a live query's body as the consumer
/// pulls it, so it sees the stream cut at whatever boundaries the network chose — and it must agree
/// with the platform's own parser about what counts as an event, or the panel's counts would
/// disagree with the answers the app acted on.
/// </summary>
[TestFixture]
public class SseFramerTests
{
    public record Framed(string Name, string? Id, string Data, bool Truncated, int Bytes);

    // The one that matters most: the same event, delivered in two pieces cut at every position there
    // is, is one event every time.
    [Test]
    public void EventsAreFramedAcrossEveryChunkBoundary()
    {
        var whole = "event: result\nid: a3f1\ndata: {\"a\":1}\n\n";

        for (var split = 1; split < whole.Length; split++)
        {
            var frames = Frame(4096, whole[..split], whole[split..]);

            Assert.That(frames, Has.Count.EqualTo(1), $"split at {split}");
            Assert.That(frames[0].Name, Is.EqualTo(ScryLive.Result), $"split at {split}");
            Assert.That(frames[0].Id, Is.EqualTo("a3f1"), $"split at {split}");
            Assert.That(frames[0].Data, Is.EqualTo("{\"a\":1}"), $"split at {split}");
            Assert.That(frames[0].Bytes, Is.EqualTo(whole.Length), $"split at {split}");
        }
    }

    // The format says an event with an empty data buffer is not dispatched; .NET's parser dispatches
    // on a data line having been seen, empty or not. This server always writes one, which is why the
    // client sees the two events that carry nothing — so this follows .NET, not the format.
    [Test]
    public void AnEventIsOnlyFramedWhereADataLineWasSeen()
    {
        Assert.That(Frame(4096, "event: ping\n\n"), Is.Empty);

        var frames = Frame(4096, "event: ping\ndata: \n\n");
        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].Name, Is.EqualTo(ScryLive.Ping));
        Assert.That(frames[0].Data, Is.Empty);
    }

    [Test]
    public void LinesEndWithAnyOfTheThreeEndings()
    {
        foreach (var ending in (string[]) ["\n", "\r\n", "\r"])
        {
            var text = $"event: ping{ending}data: x{ending}{ending}";
            var frames = Frame(4096, text);

            Assert.That(frames, Has.Count.EqualTo(1), ending);
            Assert.That(frames[0].Data, Is.EqualTo("x"), ending);
            Assert.That(frames[0].Bytes, Is.EqualTo(text.Length), ending);
        }
    }

    // A carriage return ending one read and its newline beginning the next is one line ending, not
    // two — and two would frame an event early.
    [Test]
    public void ACarriageReturnSplitFromItsNewlineIsOneEnding()
    {
        var frames = Frame(4096, "event: ping\r", "\ndata: x\r\n\r\n");

        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].Name, Is.EqualTo(ScryLive.Ping));
        Assert.That(frames[0].Data, Is.EqualTo("x"));
    }

    [Test]
    public void CommentsAndUnknownFieldsAreIgnoredButCounted()
    {
        var text = ": keep alive\nevent: ping\nretry: 5000\ndata: x\n\n";
        var frames = Frame(4096, text);

        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].Name, Is.EqualTo(ScryLive.Ping));
        Assert.That(frames[0].Data, Is.EqualTo("x"));
        Assert.That(frames[0].Bytes, Is.EqualTo(text.Length));
    }

    [Test]
    public void SeveralDataLinesAreJoinedByTheNewlinesThatSeparatedThem()
    {
        var frames = Frame(4096, "event: result\ndata: one\ndata: two\n\n");

        Assert.That(frames[0].Data, Is.EqualTo("one\ntwo"));
    }

    // An answer longer than the panel keeps is listed by its size rather than shown in part, and the
    // size stays exact — it is counted off the chunks, not off what was stored.
    [Test]
    public void DataBeyondTheCapIsCutAndFlagged()
    {
        var payload = new string('x', 5000);
        var text = $"event: result\ndata: {payload}\n\n";
        var frames = Frame(64, text);

        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].Truncated, Is.True);
        Assert.That(frames[0].Data, Has.Length.LessThanOrEqualTo(64));
        Assert.That(frames[0].Bytes, Is.EqualTo(text.Length));
    }

    [Test]
    public void AByteOrderMarkLeadsTheStreamRatherThanEveryLine()
    {
        var frames = Frame(4096, "﻿event: ping\ndata: x\n\n");

        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].Name, Is.EqualTo(ScryLive.Ping));
    }

    // An event a newer server sends and this client has no name for is still worth counting: "why is
    // my client ignoring this?" is exactly what the panel is opened to answer.
    [Test]
    public void AnEventThisClientHasNoNameForIsStillFramed()
    {
        var frames = Frame(4096, "event: rebalance\ndata: {}\n\n");

        Assert.That(frames[0].Name, Is.EqualTo("rebalance"));
    }

    [Test]
    public void AnEventThatNamesNothingIsTheDefaultOne()
    {
        var frames = Frame(4096, "data: {}\n\n");

        Assert.That(frames[0].Name, Is.EqualTo("message"));
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
