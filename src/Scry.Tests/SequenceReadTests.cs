/// <summary>
/// The questions text and binary answer as sequences. Neither ever yields its elements — a string and
/// a byte[] are scalars on the wire — so every one of these folds to a single value, and the ones the
/// provider refuses are left out rather than carried into a query that would fail at execution.
/// </summary>
public class SequenceReadTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record ShiftRow(string Name);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task FirstAndLastCharacter()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Shift>("Shift")
            .Where(_ => _.Name.FirstOrDefault() == 'E' && _.Name.LastOrDefault() == 'y')
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Early");
    }

    [Test]
    public async Task BinaryLength()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Shift>("Shift")
            .Where(_ => _.Signature.Length == 3)
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Early");
    }

    // Length above zero is how the emptiness question is asked: Any() means the same and the provider
    // refuses it, so the set carries the spelling that translates.
    [Test]
    public async Task EmptinessIsAskedThroughLength()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Shift>("Shift")
            .Where(_ => _.Signature.Length > 0)
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Early");
    }

    // The compiler resolves Contains on an array to MemoryExtensions rather than Enumerable, so the
    // member arrives wrapped in a call to the span conversion operator.
    [Test]
    public async Task BinaryContains()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var rows = await client.Source<Shift>("Shift")
            .Where(_ => _.Signature.Contains((byte)0x0B))
            .Select(_ => new ShiftRow(_.Name))
            .ToListAsync();

        await Assert.That(rows.Single().Name).IsEqualTo("Early");
    }

    [Test]
    public async Task ByteAtAPosition()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var first = await client.Source<Shift>("Shift")
            .CountAsync(_ => _.Signature.First() == 0x0A);

        var second = await client.Source<Shift>("Shift")
            .CountAsync(_ => _.Signature.ElementAt(1) == 0x0B);

        using (Assert.Multiple())
        {
            await Assert.That(first).IsEqualTo(1);
            await Assert.That(second).IsEqualTo(1);
        }
    }

    // An attachment's value is the one thing no query reads, so none of these reach it. The refusal is
    // the server's, which is where it has to be: a generated client sees a handle rather than a byte[]
    // and cannot spell the question at all, and this is the same request written by hand.
    [Test]
    public async Task AnAttachmentAnswersNoneOfThem()
    {
        using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var exception = await Assert.ThrowsExactlyAsync<ScryValidationException>(
            () => client.Source<Contract>("Contract")
                .Where(_ => _.Document!.Length > 0)
                .Select(_ => new ShiftRow(_.Name))
                .ToListAsync());

        await Assert.That(exception!.Message).Contains("attachment");
    }

    // A [BinaryTransfer] member is a value, so it answers them all — what that attribute changes is
    // how the bytes travel in a response, not whether the row can be asked about them.
    [Test]
    public async Task ABinaryTransferMemberIsAnOrdinaryValue()
    {
        await using var context = TestContext.CreateSeeded();
        var client = ClientFor(context);

        var count = await client.Source<Employee>("Employee")
            .CountAsync(_ => _.Avatar.Length > 0);

        await Assert.That(count).IsEqualTo(3);
    }

    static ScryClient ClientFor(TestContext context) =>
        new((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context)));
}
