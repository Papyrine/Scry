// Arranging a response payload as a grid. The flat/nested classification is what decides whether CSV
// is offered, and it ran only through the browser suite before this moved out of App.razor.cs.
public class ResultTableTests
{
    [Test]
    public async Task TakesColumnsFromTheFirstRow()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron","status":"FullTime"}]"""));

        await Assert.That(table!.Columns).IsEquivalentTo(["name", "status"], CollectionOrdering.Matching);
        await Assert.That(table.Rows).Count().IsEqualTo(1);
        await Assert.That(table.Rows[0]).IsEquivalentTo(["Aaron", "FullTime"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task KeepsRowsInOrder()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron"},{"name":"Carol"}]"""));

        await Assert.That(table!.Rows.Select(_ => _[0])).IsEquivalentTo(["Aaron", "Carol"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task KeepsTheServersOwnRowsAlongsideTheRenderedCells()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron"}]"""));

        await Assert.That(table!.PayloadRows[0].GetProperty("name").GetString()).IsEqualTo("Aaron");
    }

    // Every cell a scalar: a grid can hold it, so CSV is on offer.
    [Test]
    public async Task ClassifiesAScalarProjectionAsFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron","active":true,"id":1}]"""));

        await Assert.That(table!.IsFlat).IsTrue();
    }

    // Projecting into a navigation nests an object inside the row, and a tree has no faithful CSV.
    [Test]
    public async Task ClassifiesANestedObjectAsNotFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron","department":{"name":"Ops"}}]"""));

        await Assert.That(table!.IsFlat).IsFalse();
    }

    [Test]
    public async Task ClassifiesACollectionAsNotFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"tags":["a"]}]"""));

        await Assert.That(table!.IsFlat).IsFalse();
    }

    // One nested row among flat ones is still a tree.
    [Test]
    public async Task ClassifiesAMixedResultAsNotFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"a":1},{"a":{"b":2}}]"""));

        await Assert.That(table!.IsFlat).IsFalse();
    }

    [Test]
    public async Task ClassifiesANullCellAsFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"manager":null}]"""));

        await Assert.That(table!.IsFlat).IsTrue();
    }

    [Test]
    public async Task ReadsAnEmptyListAsAnEmptyTable()
    {
        var table = ResultTable.FromList(Payload("[]"));

        await Assert.That(table!.Columns).IsEmpty();
        await Assert.That(table.Rows).IsEmpty();
    }

    // Non-object entries are skipped rather than rendered as a row with no members.
    [Test]
    public async Task SkipsAnEntryThatIsNotAnObject()
    {
        var table = ResultTable.FromList(Payload("""[1,{"name":"Aaron"},"x"]"""));

        await Assert.That(table!.Columns).IsEquivalentTo(["name"], CollectionOrdering.Matching);
        await Assert.That(table.Rows).Count().IsEqualTo(1);
    }

    [Test]
    public async Task RefusesAPayloadThatIsNotAList() =>
        await Assert.That(ResultTable.FromList(Payload("""{"name":"Aaron"}"""))).IsNull();

    // A Single result renders through the same markup a list does.
    [Test]
    public async Task BuildsAOneRowTableFromASingleRow()
    {
        var table = ResultTable.FromRow(Payload("""{"name":"Aaron","status":"FullTime"}"""));

        await Assert.That(table.Columns).IsEquivalentTo(["name", "status"], CollectionOrdering.Matching);
        await Assert.That(table.Rows).Count().IsEqualTo(1);
        await Assert.That(table.Rows[0]).IsEquivalentTo(["Aaron", "FullTime"], CollectionOrdering.Matching);
        await Assert.That(table.IsFlat).IsTrue();
    }

    static JsonElement Payload(string json) =>
        JsonDocument.Parse(json).RootElement;
}
