/// <summary>
/// A batch entry that fails is answered with an error, so the parts it added before failing are taken
/// back, and what they were charged is given back to the response's budget for the entries after it.
/// </summary>
public class BinaryPartCollectorTests
{
    [Test]
    public async Task RewindingDropsThePartsAndGivesBackWhatTheyCost()
    {
        var budget = new ResponseBudget(10);
        var collector = new BinaryPartCollector(budget);
        collector.Add([1, 2, 3]);
        var mark = collector.Count;
        collector.Add([4, 5, 6, 7, 8, 9]);

        collector.Rewind(mark);

        using (Assert.Multiple())
        {
            await Assert.That(collector.Count).IsEqualTo(1);
            await Assert.That(budget.Fits(7)).IsTrue();
            await Assert.That(budget.Fits(8)).IsFalse();
        }
    }
}
