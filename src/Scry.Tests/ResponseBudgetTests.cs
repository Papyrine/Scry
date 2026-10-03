/// <summary>
/// What <see cref="ScryOptions.MaxResponseBytes"/> counts and when it refuses. The count is of what a
/// response carries rather than what it is holding, a refusal costs the budget nothing, and a JSON
/// writer that asks for more room than it uses is not turned away for asking.
/// </summary>
public class ResponseBudgetTests
{
    // A response that drains holds little at any moment; what it has sent still counts.
    [Test]
    public async Task CountsWhatADrainedResponseSentRatherThanWhatItHolds()
    {
        var context = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream()
            }
        };
        using var spill = new ResponseSpill(context, 100);
        spill.AllowSpill(true);
        var output = new ResponseBudget(180).Charging(spill.Output);

        Fill(output, 150);
        await spill.DrainAsync();

        Assert.ThrowsExactly<ScryValidationException>(() => Fill(output, 50));
    }

    [Test]
    public async Task ARefusalChargesNothing()
    {
        var budget = new ResponseBudget(100);
        budget.Spend(60);

        Assert.ThrowsExactly<ScryValidationException>(() => budget.Spend(50));

        // The forty that are left are still there for whatever asks next.
        await Assert.That(() => budget.Spend(40)).ThrowsNothing();
    }

    [Test]
    public async Task RefusesOnceThenDropsWhatIsCommitted()
    {
        using var inner = new PooledBufferWriter();
        var output = new ResponseBudget(10).Charging(inner);

        Fill(output, 8);
        Assert.ThrowsExactly<ScryValidationException>(() => Fill(output, 8));

        // What a JSON writer commits as it is disposed, after the refusal: dropped rather than refused
        // again, and never passed on to the buffer behind it.
        await Assert.That(() => Fill(output, 8)).ThrowsNothing();
        await Assert.That(inner.WrittenCount).IsEqualTo(8);
    }

    // The writer flushes what it was holding as it is disposed. A second refusal from there would
    // replace the first, so it must not happen.
    [Test]
    public async Task AJsonWriterRefusedMidWriteFailsOnce()
    {
        using var inner = new PooledBufferWriter();
        var output = new ResponseBudget(1000).Charging(inner);

        var exception = Assert.ThrowsExactly<ScryValidationException>(() =>
        {
            using var json = new Utf8JsonWriter(output);
            json.WriteStartArray();
            for (var item = 0; item < 100; item++)
            {
                json.WriteStringValue(new string('x', 100));
            }

            json.WriteEndArray();
        });

        await Assert.That(exception.Message).Contains("larger than this server allows (1000 bytes)");
    }

    // A JSON writer asks for three bytes per character of a string it may have to transcode. Refusing on
    // the ask would turn this value away at a third of the limit.
    [Test]
    public async Task AValueWellInsideTheLimitIsNotRefusedForTheRoomItAskedFor()
    {
        using var inner = new PooledBufferWriter();
        var output = new ResponseBudget(1500).Charging(inner);

        await using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStringValue(new string('x', 1000));
        }

        await Assert.That(inner.WrittenCount).IsEqualTo(1002);
    }

    // A batch entry is checked as it is written and charged only once it is copied into the envelope,
    // so one that is refused leaves the budget to the entries after it.
    [Test]
    public async Task ACheckingWriterSpendsNothing()
    {
        var budget = new ResponseBudget(100);
        using var entry = new PooledBufferWriter();
        var output = budget.Checking(entry);

        Fill(output, 80);

        using (Assert.Multiple())
        {
            await Assert.That(budget.Fits(100)).IsTrue();
            Assert.ThrowsExactly<ScryValidationException>(() => Fill(output, 30));
        }
    }

    [Test]
    public async Task ACheckingWriterCountsWhatTheResponseAlreadyCarries()
    {
        var budget = new ResponseBudget(100);
        budget.Spend(70);
        using var entry = new PooledBufferWriter();
        var output = budget.Checking(entry);

        Fill(output, 30);

        await Assert.That(() => Fill(output, 1)).Throws<ScryValidationException>();
    }

    // A part is spent as it is collected, so a result past the limit stops at the part that crossed it
    // rather than collecting the rest first.
    [Test]
    public async Task ACollectorRefusesThePartThatCrossesTheLimit()
    {
        var collector = new BinaryPartCollector(new(100));
        collector.Add(new byte[60]);

        Assert.ThrowsExactly<ScryValidationException>(() => collector.Add(new byte[60]));

        await Assert.That(collector.Count).IsEqualTo(1);
    }

    [Test]
    public async Task NoLimitIsTheDefault() =>
        await Assert.That(ResponseBudget.For(new(typeof(TestContext)))).IsNull();

    [Test]
    public async Task ALimitBelowOneIsRefusedAtStartup()
    {
        var exception = Assert.Throws<Exception>(() => Create(0));

        await Assert.That(exception.Message).Contains($"ScryOptions.{nameof(ScryOptions.MaxResponseBytes)} ");
    }

    [Test]
    public async Task ALimitOfOneIsAccepted() =>
        await Assert.That(() => Create(1)).ThrowsNothing();

    static ScryProcessor Create(int limit) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.MaxResponseBytes = limit;
        });

    static void Fill(IBufferWriter<byte> output, int count)
    {
        output.GetSpan(count)[..count].Fill(1);
        output.Advance(count);
    }
}
