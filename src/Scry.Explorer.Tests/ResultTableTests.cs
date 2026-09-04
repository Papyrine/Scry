// Arranging a response payload as a grid. The flat/nested classification is what decides whether CSV
// is offered, and it ran only through the browser suite before this moved out of App.razor.cs.
[TestFixture]
public class ResultTableTests
{
    [Test]
    public void TakesColumnsFromTheFirstRow()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron","status":"FullTime"}]"""));

        Assert.That(table!.Columns, Is.EqualTo(["name", "status"]));
        Assert.That(table.Rows, Has.Count.EqualTo(1));
        Assert.That(table.Rows[0], Is.EqualTo(["Aaron", "FullTime"]));
    }

    [Test]
    public void KeepsRowsInOrder()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron"},{"name":"Carol"}]"""));

        Assert.That(table!.Rows.Select(_ => _[0]), Is.EqualTo(["Aaron", "Carol"]));
    }

    [Test]
    public void KeepsTheServersOwnRowsAlongsideTheRenderedCells()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron"}]"""));

        Assert.That(table!.PayloadRows[0].GetProperty("name").GetString(), Is.EqualTo("Aaron"));
    }

    // Every cell a scalar: a grid can hold it, so CSV is on offer.
    [Test]
    public void ClassifiesAScalarProjectionAsFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron","active":true,"id":1}]"""));

        Assert.That(table!.IsFlat);
    }

    // Projecting into a navigation nests an object inside the row, and a tree has no faithful CSV.
    [Test]
    public void ClassifiesANestedObjectAsNotFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"name":"Aaron","department":{"name":"Ops"}}]"""));

        Assert.That(table!.IsFlat, Is.False);
    }

    [Test]
    public void ClassifiesACollectionAsNotFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"tags":["a"]}]"""));

        Assert.That(table!.IsFlat, Is.False);
    }

    // One nested row among flat ones is still a tree.
    [Test]
    public void ClassifiesAMixedResultAsNotFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"a":1},{"a":{"b":2}}]"""));

        Assert.That(table!.IsFlat, Is.False);
    }

    [Test]
    public void ClassifiesANullCellAsFlat()
    {
        var table = ResultTable.FromList(Payload("""[{"manager":null}]"""));

        Assert.That(table!.IsFlat);
    }

    [Test]
    public void ReadsAnEmptyListAsAnEmptyTable()
    {
        var table = ResultTable.FromList(Payload("[]"));

        Assert.That(table!.Columns, Is.Empty);
        Assert.That(table.Rows, Is.Empty);
    }

    // Non-object entries are skipped rather than rendered as a row with no members.
    [Test]
    public void SkipsAnEntryThatIsNotAnObject()
    {
        var table = ResultTable.FromList(Payload("""[1,{"name":"Aaron"},"x"]"""));

        Assert.That(table!.Columns, Is.EqualTo(["name"]));
        Assert.That(table.Rows, Has.Count.EqualTo(1));
    }

    [Test]
    public void RefusesAPayloadThatIsNotAList() =>
        Assert.That(ResultTable.FromList(Payload("""{"name":"Aaron"}""")), Is.Null);

    // A Single result renders through the same markup a list does.
    [Test]
    public void BuildsAOneRowTableFromASingleRow()
    {
        var table = ResultTable.FromRow(Payload("""{"name":"Aaron","status":"FullTime"}"""));

        Assert.That(table.Columns, Is.EqualTo(["name", "status"]));
        Assert.That(table.Rows, Has.Count.EqualTo(1));
        Assert.That(table.Rows[0], Is.EqualTo(["Aaron", "FullTime"]));
        Assert.That(table.IsFlat);
    }

    static JsonElement Payload(string json) =>
        JsonDocument.Parse(json).RootElement;
}
