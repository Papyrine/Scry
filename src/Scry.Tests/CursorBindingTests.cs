/// <summary>
/// A cursor resumes the ordering it was issued for. Each case below sends the cursor back with an
/// ordering of the <em>same shape</em> — same key count, same key types — so nothing but the ordering
/// stamp distinguishes them; before it, every one of these seeked happily and answered with a
/// plausible, silently wrong page.
/// </summary>
[TestFixture]
public class CursorBindingTests
{
    [Test]
    public void RejectsAFlippedDirection()
    {
        // The sharpest case: same source, same column, same type, same key count. Only the direction
        // differs, and the seek reads its direction from the new request — so the predicate becomes
        // "before Alice" while claiming to be the page after her.
        using var context = TestContext.CreateSeeded();
        var cursor = CursorFor(context, "Employee", new OrderByOp(new MemberNode(["Name"]), Descending: false));

        var exception = Assert.Throws<ScryValidationException>(
            () => Page(context, "Employee", cursor, new OrderByOp(new MemberNode(["Name"]), Descending: true)))!;

        Assert.That(exception.Message, Does.Contain("does not match the query's ordering"));
    }

    [Test]
    public void RejectsACursorFromAnotherSource()
    {
        // Employee ordered by Name and Order ordered by Region both seek (string, int) — the appended
        // primary key makes the shapes identical — so only the source and column names part them.
        using var context = TestContext.CreateSeeded();
        var cursor = CursorFor(context, "Employee", new OrderByOp(new MemberNode(["Name"]), Descending: false));

        var exception = Assert.Throws<ScryValidationException>(
            () => Page(context, "Order", cursor, new OrderByOp(new MemberNode(["Region"]), Descending: false)))!;

        Assert.That(exception.Message, Does.Contain("does not match the query's ordering"));
    }

    [Test]
    public void RejectsADifferentKeyCount()
    {
        // What the old key-count check caught; the stamp subsumes it rather than sitting beside it.
        using var context = TestContext.CreateSeeded();
        var cursor = CursorFor(context, "Employee", new OrderByOp(new MemberNode(["Name"]), Descending: false));

        Assert.Throws<ScryValidationException>(
            () => Page(
                context,
                "Employee",
                cursor,
                new OrderByOp(new MemberNode(["DepartmentId"]), Descending: false),
                new ThenByOp(new MemberNode(["Name"]), Descending: false)));
    }

    [Test]
    public void ResumesTheSameOrdering()
    {
        using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = CursorFor(context, "Employee", ordering);

        var page = Page(context, "Employee", cursor, ordering);

        Assert.That(page.GetProperty("items").GetArrayLength(), Is.GreaterThan(0));
    }

    [Test]
    public void ResumesThroughAChangedFilter()
    {
        // The deliberate limit of the stamp: it binds the ordering, not the whole pipeline. Narrowing
        // the set between pages leaves "the rows of this set ordered after this key" well defined, so
        // it stays legal — where hashing the pipeline would have refused it.
        using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = CursorFor(context, "Employee", ordering);

        var page = Page(
            context,
            "Employee",
            cursor,
            new WhereOp(new MemberNode(["Active"])),
            ordering);

        Assert.That(page.GetProperty("items"), Is.Not.Null);
    }

    // Fleet and Machine both spell their keys Name and Id, so a cursor over the fleets and one over
    // the machines a flatten reaches seek the same (string, int). Before the flatten was stamped, the
    // one resumed the other and seeked the machines past a fleet's values: a plausible, wrong page.
    [Test]
    public void RejectsACursorFromTheRootOnAFlattenedQuery()
    {
        using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = CursorFor(context, "Fleet", ordering);

        var exception = Assert.Throws<ScryValidationException>(
            () => Page(context, "Fleet", cursor, new SelectManyOp(["Machines"]), ordering))!;

        Assert.That(exception.Message, Does.Contain("does not match the query's ordering"));
    }

    [Test]
    public void RejectsACursorFromAFlattenedQueryOnTheRoot()
    {
        using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = CursorFor(context, "Fleet", new SelectManyOp(["Machines"]), ordering);

        var exception = Assert.Throws<ScryValidationException>(
            () => Page(context, "Fleet", cursor, ordering))!;

        Assert.That(exception.Message, Does.Contain("does not match the query's ordering"));
    }

    [Test]
    public void ResumesAFlattenedOrdering()
    {
        using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = CursorFor(context, "Fleet", new SelectManyOp(["Machines"]), ordering);

        var page = Page(context, "Fleet", cursor, new SelectManyOp(["Machines"]), ordering);

        Assert.That(page.GetProperty("items").GetArrayLength(), Is.GreaterThan(0));
    }

    // The same for a narrowing: the vehicles are assets, ordered by the same members, but the rows
    // a cursor over them describes are not the rows the base query reads.
    [Test]
    public void RejectsACursorFromANarrowedQueryOnTheBase()
    {
        using var context = TestContext.CreateSeeded();
        var ordering = new OrderByOp(new MemberNode(["Name"]), Descending: false);
        var cursor = CursorFor(context, "Asset", new OfTypeOp("Vehicle"), ordering);

        var exception = Assert.Throws<ScryValidationException>(
            () => Page(context, "Asset", cursor, ordering))!;

        Assert.That(exception.Message, Does.Contain("does not match the query's ordering"));
    }

    static readonly byte[] sharedKey = "a key the tests share"u8.ToArray();

    static readonly OrderByOp byName = new(new MemberNode(["Name"]), Descending: false);

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
    public void RejectsACursorValueThatDoesNotParseAsTheKey()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Keyed(sharedKey);
        var cursor = CursorCodec.Encode([("Ann", ClrTypeTag.String), ("abc", ClrTypeTag.Int32)], EmployeeByNameOrder(), sharedKey);

        var exception = Assert.Throws<ScryValidationException>(
            () => processor.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context))!;

        Assert.That(exception.Message, Does.Contain("not a valid Int32 value"));
    }

    // A null where the key is not nullable seeks past nothing: an empty page or a rejection, and
    // never a fault.
    [Test]
    public void ANullCursorValueForANonNullableKeyDoesNotFault()
    {
        using var context = TestContext.CreateSeeded();
        var processor = Keyed(sharedKey);
        var cursor = CursorCodec.Encode([(null, ClrTypeTag.Null), ("1", ClrTypeTag.Int32)], EmployeeByNameOrder(), sharedKey);

        try
        {
            var page = processor.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context);
            Assert.That(page.Payload.GetProperty("items").GetArrayLength(), Is.Zero);
        }
        catch (ScryValidationException)
        {
            // Also acceptable: the value is refused rather than seeked.
        }
    }

    // CursorKey is what lets a cursor outlive the process that minted it: two servers configured with
    // the same key read each other's cursors.
    [Test]
    public void ACursorKeyLetsAnotherProcessorResumeTheCursor()
    {
        using var context = TestContext.CreateSeeded();
        var first = Keyed(sharedKey);
        var second = Keyed(sharedKey);
        var cursor = first.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1)]), context).Payload.GetProperty("cursor").GetString()!;

        var page = second.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context);

        Assert.That(page.Payload.GetProperty("items").GetArrayLength(), Is.GreaterThan(0));
    }

    [Test]
    public void ADifferentCursorKeyRefusesTheCursor()
    {
        using var context = TestContext.CreateSeeded();
        var first = Keyed(sharedKey);
        var other = Keyed("another key"u8.ToArray());
        var cursor = first.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1)]), context).Payload.GetProperty("cursor").GetString()!;

        Assert.Throws<ScryValidationException>(
            () => other.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context));
    }

    // Without a key, cursors are per process: every processor in it shares one ephemeral key, so a
    // second processor here still reads them, and a restart is what loses them.
    [Test]
    public void WithoutACursorKeyCursorsArePerProcess()
    {
        using var context = TestContext.CreateSeeded();
        var first = Keyed(null);
        var second = Keyed(null);
        var cursor = first.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1)]), context).Payload.GetProperty("cursor").GetString()!;

        Assert.DoesNotThrow(() => second.Execute(QueryRequest.Create("Employee", [byName, new PageOp(Size: 1, cursor)]), context));
    }

    // A first page small enough to leave more behind, so the response carries a cursor to resume with.
    static string CursorFor(TestContext context, string root, params QueryOp[] pipeline)
    {
        var request = QueryRequest.Create(root, [.. pipeline, new PageOp(Size: 1)]);
        var payload = SharedProcessor.Instance.Execute(request, context).Payload;
        var cursor = payload.GetProperty("cursor").GetString();

        Assert.That(cursor, Is.Not.Null, "the first page should issue a cursor");
        return cursor!;
    }

    static JsonElement Page(TestContext context, string root, string cursor, params QueryOp[] pipeline)
    {
        var request = QueryRequest.Create(root, [.. pipeline, new PageOp(Size: 1, cursor)]);
        return SharedProcessor.Instance.Execute(request, context).Payload;
    }
}
