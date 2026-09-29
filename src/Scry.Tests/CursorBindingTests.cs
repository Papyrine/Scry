/// <summary>
/// A cursor resumes the ordering it was issued for. Each case below sends the cursor back with an
/// ordering of the <em>same shape</em> — same key count, same key types — so nothing but the ordering
/// stamp distinguishes them; before it, every one of these seeked happily and answered with a
/// plausible, silently wrong page.
/// </summary>
public class CursorBindingTests
{
    [Test]
    public async Task RejectsAFlippedDirection()
    {
        // The sharpest case: same source, same column, same type, same key count. Only the direction
        // differs, and the seek reads its direction from the new request — so the predicate becomes
        // "before Alice" while claiming to be the page after her.
        await using var context = TestContext.CreateSeeded();
        var cursor = await CursorFor(context, "Employee", new OrderByOp(new MemberNode(["Name"]), Descending: false));

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => Page(context, "Employee", cursor, new OrderByOp(new MemberNode(["Name"]), Descending: true)));

        await Assert.That(exception.Message).Contains("does not match the query's ordering");
    }

    [Test]
    public async Task RejectsACursorFromAnotherSource()
    {
        // Employee ordered by Name and Order ordered by Region both seek (string, int) — the appended
        // primary key makes the shapes identical — so only the source and column names part them.
        await using var context = TestContext.CreateSeeded();
        var cursor = await CursorFor(context, "Employee", new OrderByOp(new MemberNode(["Name"]), Descending: false));

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => Page(context, "Order", cursor, new OrderByOp(new MemberNode(["Region"]), Descending: false)));

        await Assert.That(exception.Message).Contains("does not match the query's ordering");
    }

    [Test]
    public async Task RejectsADifferentKeyCount()
    {
        // What the old key-count check caught; the stamp subsumes it rather than sitting beside it.
        await using var context = TestContext.CreateSeeded();
        var cursor = await CursorFor(context, "Employee", new OrderByOp(new MemberNode(["Name"]), Descending: false));

        Assert.ThrowsExactly<ScryValidationException>(
            () => Page(
                context,
                "Employee",
                cursor,
                new OrderByOp(new MemberNode(["DepartmentId"]), Descending: false),
                new ThenByOp(new MemberNode(["Name"]), Descending: false)));
    }

    [Test]
    public async Task ResumesTheSameOrdering()
    {
        await using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = await CursorFor(context, "Employee", ordering);

        var page = Page(context, "Employee", cursor, ordering);

        await Assert.That(page.GetProperty("items").GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task ResumesThroughAChangedFilter()
    {
        // The deliberate limit of the stamp: it binds the ordering, not the whole pipeline. Narrowing
        // the set between pages leaves "the rows of this set ordered after this key" well defined, so
        // it stays legal — where hashing the pipeline would have refused it.
        await using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = await CursorFor(context, "Employee", ordering);

        var page = Page(
            context,
            "Employee",
            cursor,
            new WhereOp(new MemberNode(["Active"])),
            ordering);

        await Assert.That(page.GetProperty("items")).IsNotNull();
    }

    // Fleet and Machine both spell their keys Name and Id, so a cursor over the fleets and one over
    // the machines a flatten reaches seek the same (string, int). Before the flatten was stamped, the
    // one resumed the other and seeked the machines past a fleet's values: a plausible, wrong page.
    [Test]
    public async Task RejectsACursorFromTheRootOnAFlattenedQuery()
    {
        await using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = await CursorFor(context, "Fleet", ordering);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => Page(context, "Fleet", cursor, new SelectManyOp(["Machines"]), ordering));

        await Assert.That(exception.Message).Contains("does not match the query's ordering");
    }

    [Test]
    public async Task RejectsACursorFromAFlattenedQueryOnTheRoot()
    {
        await using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = await CursorFor(context, "Fleet", new SelectManyOp(["Machines"]), ordering);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => Page(context, "Fleet", cursor, ordering));

        await Assert.That(exception.Message).Contains("does not match the query's ordering");
    }

    [Test]
    public async Task ResumesAFlattenedOrdering()
    {
        await using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = await CursorFor(context, "Fleet", new SelectManyOp(["Machines"]), ordering);

        var page = Page(context, "Fleet", cursor, new SelectManyOp(["Machines"]), ordering);

        await Assert.That(page.GetProperty("items").GetArrayLength()).IsGreaterThan(0);
    }

    // The same for a narrowing: the vehicles are assets, ordered by the same members, but the rows
    // a cursor over them describes are not the rows the base query reads.
    [Test]
    public async Task RejectsACursorFromANarrowedQueryOnTheBase()
    {
        await using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = await CursorFor(context, "Asset", new OfTypeOp("Vehicle"), ordering);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => Page(context, "Asset", cursor, ordering));

        await Assert.That(exception.Message).Contains("does not match the query's ordering");
    }

    static byte[] sharedKey = "a key the tests share"u8.ToArray();

    static OrderByOp byName = new(new MemberNode(["Name"]), Descending: false);

    // The stamp the executor would mint for Employee ordered by Name: the client's key, then the
    // primary key it appends as the tiebreaker.
    static string EmployeeByNameOrder() =>
        CursorCodec.OrderStamp("Employee", [], [(new MemberNode(["Name"]), false), (new MemberNode(["Id"]), false)]);

    static ScryProcessor Keyed(byte[]? key) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.CursorKey = key;
        });

    // A cursor is sealed, so its values are the server's own — but the server that minted it may
    // have had a different model, and the seek parses each value as the key's type. One that does
    // not parse is a rejection, as the same text in a predicate would be.
    [Test]
    public async Task RejectsACursorValueThatDoesNotParseAsTheKey()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = Keyed(sharedKey);
        var cursor = CursorCodec.Encode([("Ann", ClrTypeTag.String), ("abc", ClrTypeTag.Int32)], EmployeeByNameOrder(), sharedKey);

        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => processor.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context));

        await Assert.That(exception.Message).Contains("not a valid Int32 value");
    }

    // A null where the key is not nullable seeks past nothing: an empty page or a rejection, and
    // never a fault.
    [Test]
    public async Task ANullCursorValueForANonNullableKeyDoesNotFault()
    {
        await using var context = TestContext.CreateSeeded();
        var processor = Keyed(sharedKey);
        var cursor = CursorCodec.Encode([(null, ClrTypeTag.Null), ("1", ClrTypeTag.Int32)], EmployeeByNameOrder(), sharedKey);

        try
        {
            var page = processor.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context);
            await Assert.That(page.Payload.GetProperty("items").GetArrayLength()).IsZero();
        }
        catch (ScryValidationException)
        {
            // Also acceptable: the value is refused rather than seeked.
        }
    }

    // CursorKey is what lets a cursor outlive the process that minted it: two servers configured with
    // the same key read each other's cursors.
    [Test]
    public async Task ACursorKeyLetsAnotherProcessorResumeTheCursor()
    {
        await using var context = TestContext.CreateSeeded();
        var first = Keyed(sharedKey);
        var second = Keyed(sharedKey);
        var cursor = first.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1)]), context).Payload.GetProperty("cursor").GetString()!;

        var page = second.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context);

        await Assert.That(page.Payload.GetProperty("items").GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public void ADifferentCursorKeyRefusesTheCursor()
    {
        using var context = TestContext.CreateSeeded();
        var first = Keyed(sharedKey);
        var other = Keyed("another key"u8.ToArray());
        var cursor = first.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1)]), context).Payload.GetProperty("cursor").GetString()!;

        Assert.ThrowsExactly<ScryValidationException>(
            () => other.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context));
    }

    // Without a key, cursors are per process: every processor in it shares one ephemeral key, so a
    // second processor here still reads them, and a restart is what loses them.
    [Test]
    public async Task WithoutACursorKeyCursorsArePerProcess()
    {
        await using var context = TestContext.CreateSeeded();
        var first = Keyed(null);
        var second = Keyed(null);
        var cursor = first.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1)]), context).Payload.GetProperty("cursor").GetString()!;

        await Assert.That(() => second.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context)).ThrowsNothing();
    }

    // A first page small enough to leave more behind, so the response carries a cursor to resume with.
    static async Task<string> CursorFor(TestContext context, string root, params QueryOp[] pipeline)
    {
        var request = QueryRequest.Create(root, [.. pipeline, new PageOp(Size: 1)]);
        var payload = SharedProcessor.Instance.Execute(request, context).Payload;
        var cursor = payload.GetProperty("cursor").GetString();

        await Assert.That(cursor).IsNotNull().Because("the first page should issue a cursor");
        return cursor!;
    }

    static JsonElement Page(TestContext context, string root, string cursor, params QueryOp[] pipeline)
    {
        var request = QueryRequest.Create(root, [.. pipeline, new PageOp(Size: 1, cursor)]);
        return SharedProcessor.Instance.Execute(request, context).Payload;
    }
}
