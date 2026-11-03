/// <summary>
/// A POCO source pages by cursor where it has a key to break ties with — derived as an attachment's
/// is: a [Key], an Id, or a {Type}Id — and is ordered by members its CLR declarations say are never
/// null. Without either it pages by offset with no cursor, as a view does. Text is sought past
/// ordinally, the way an in-memory source orders it.
/// </summary>
public class PocoCursorTests
{
    const string keyed =
        """
        [QueryablePoco]
        public class Fruit
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string? Note { get; set; }

            public static IEnumerable<Fruit> Rows() =>
            [
                new Fruit { Id = 1, Name = "apple" },
                new Fruit { Id = 2, Name = "Banana" },
                new Fruit { Id = 3, Name = "cherry" },
                new Fruit { Id = 4, Name = "Banana" }
            ];
        }
        """;

    // Ordinally a capital sorts before every lowercase letter, so the two Bananas come first, told
    // apart by the key the server breaks the tie with.
    [Test]
    public async Task AKeyedPocoIsPagedByCursorThroughEveryRow()
    {
        var (processor, data) = CompiledPoco.Start(keyed, "Fruit");
        await using var _ = data;

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = Page(processor, data, "Name", cursor);
            seen.AddRange(page.Items.Select(_ => $"{_.GetProperty("name").GetString()}#{_.GetProperty("id").GetInt32()}"));
            cursor = page.Cursor;
        }
        while (cursor is not null);

        await Assert.That(seen).IsEquivalentTo(["Banana#2", "Banana#4", "apple#1", "cherry#3"], CollectionOrdering.Matching);
    }

    // Nothing says the text is there, so a row could hold a null the seek cannot compare past.
    [Test]
    public async Task OrderedByAMemberThatMayBeNullItHasNoCursor()
    {
        var (processor, data) = CompiledPoco.Start(keyed, "Fruit");
        await using var _ = data;

        var page = Page(processor, data, "Note", cursor: null);

        using (Assert.Multiple())
        {
            await Assert.That(page.HasMore).IsTrue();
            await Assert.That(page.Cursor).IsNull();
        }
    }

    // No [Key], no Id, no {Type}Id: nothing to break a tie with, so no order is total.
    [Test]
    public async Task APocoWithNoKeyHasNoCursor()
    {
        var (processor, data) = CompiledPoco.Start(
            """
            [QueryablePoco]
            public class Season
            {
                public string Name { get; set; } = "";

                public static IEnumerable<Season> Rows() =>
                [
                    new Season { Name = "Spring" },
                    new Season { Name = "Summer" }
                ];
            }
            """,
            "Season");
        await using var _ = data;

        var page = Page(processor, data, "Name", cursor: null, source: "Season", select: false);

        using (Assert.Multiple())
        {
            await Assert.That(page.HasMore).IsTrue();
            await Assert.That(page.Cursor).IsNull();
        }
    }

    static (List<JsonElement> Items, bool HasMore, string? Cursor) Page(
        ScryProcessor processor,
        Microsoft.EntityFrameworkCore.DbContext data,
        string orderBy,
        string? cursor,
        string source = "Fruit",
        bool select = true)
    {
        List<QueryOp> pipeline = [new OrderByOp(new MemberNode([orderBy]), Descending: false)];
        if (select)
        {
            pipeline.Add(new SelectOp(new([new("Id", new NodeValue(new MemberNode(["Id"]))), new("Name", new NodeValue(new MemberNode(["Name"])))])));
        }

        pipeline.Add(new PageOp(Size: 1, Cursor: cursor));
        var payload = processor.Execute(QueryRequest.Create(source, pipeline), data).Payload;
        string? next = null;
        if (payload.TryGetProperty("cursor", out var written))
        {
            next = written.GetString();
        }

        return ([.. payload.GetProperty("items").EnumerateArray()], payload.GetProperty("hasMore").GetBoolean(), next);
    }
}
