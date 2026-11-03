/// <summary>
/// <c>MaxNavigationDepth</c> bounds each path on its own, and a subquery's predicate is read from the
/// collection's element: so the path to the collection and the path inside it are each held to the
/// limit, and a subquery reaches as far as twice it from the row. Pinned here so the effective bound is
/// a decision rather than a surprise — under a limit of four a subquery reaches eight segments (four to
/// the collection, four on from its element), and one more either side is refused.
/// </summary>
/// <remarks>
/// On a model compiled for these tests alone: the shared one has no collection whose elements lead
/// anywhere. Depth is the validator's rule, decided before anything is rebound, so the request is only
/// validated.
/// </remarks>
public class SubqueryDepthTests
{
    static Lazy<(Schema Schema, ScryOptions Options)> folders = new(() =>
    {
        var (assembly, _) = CompiledModel.Compile(
            """
            public sealed class Context : DbContext
            {
                public DbSet<Folder> Folders => Set<Folder>();
            }

            [Queryable]
            public class Folder
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
                public Folder? Parent { get; set; }
                [QueryableCollection]
                public List<Folder> Children { get; set; } = [];
            }
            """);
        var options = new ScryOptions(assembly.GetType("Context")!);
        return (Schema.Build(options), options);
    });

    // Four segments to the collection, four inside it: each at the limit, eight from the row.
    [Test]
    public async Task EachPathMayReachTheLimitSoASubqueryReachesTwiceIt()
    {
        var source = Validate(
            ["Parent", "Parent", "Parent", "Children"],
            ["Parent", "Parent", "Parent", "Name"]);

        await Assert.That(source.Name).IsEqualTo("Folder");
    }

    [Test]
    public async Task ThePathToTheCollectionIsHeldToTheLimit()
    {
        var exception = Assert.Throws<ScryValidationException>(
            () => Validate(
                ["Parent", "Parent", "Parent", "Parent", "Children"],
                ["Name"]));

        await Assert.That(exception!.Message).Contains("Subquery path is too deep");
    }

    [Test]
    public async Task ThePathInsideTheCollectionIsHeldToTheLimit()
    {
        var exception = Assert.Throws<ScryValidationException>(
            () => Validate(
                ["Children"],
                ["Parent", "Parent", "Parent", "Parent", "Name"]));

        await Assert.That(exception!.Message).Contains("path is too deep");
    }

    // Whether any folder at the end of the first path has a second path reading "root".
    static ScrySource Validate(IReadOnlyList<string> collection, IReadOnlyList<string> inside)
    {
        var (schema, options) = folders.Value;
        var request = QueryRequest.Create(
            "Folder",
            [
                new CountOp(
                    new SubqueryNode(
                        collection,
                        SubqueryFn.Any,
                        Predicate: new BinaryNode(BinaryOp.Equal, new MemberNode(inside), new ConstNode("root", ClrTypeTag.String))))
            ]);

        return new QueryValidator(schema, options).Validate(request);
    }
}
