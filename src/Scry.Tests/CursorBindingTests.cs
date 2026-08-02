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
