using Microsoft.EntityFrameworkCore;

/// <summary>
/// The database's own change marker as what tells a live query to run again. The write here goes
/// through a context nothing is watching — as a write from another node, another system, or a script
/// run by hand would — so the marker is the only thing that could have noticed it.
/// </summary>
[TestFixture]
public class DeltaChangesTests
{
    [Test]
    public async Task AWriteNothingReportedStillReachesALiveQuery()
    {
        await using var server = await ScryTestServer.StartAsync(deltaChanges: true);
        var query = new ScryQuery(server.CreateScryClient());

        await using var answers = query.Employee
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Name})
            .Live()
            .GetAsyncEnumerator();
        Assert.That(await Next(answers), Is.True);
        var before = answers.Current[0].Name;

        var renamed = $"{before} (renamed)";
        await using (var writing = server.NewContext())
        {
            var employee = await EntityFrameworkQueryableExtensions.FirstAsync(writing.Employees.OrderBy(_ => _.Id));
            employee.Name = renamed;
            await writing.SaveChangesAsync();
        }

        Assert.That(await Next(answers), Is.True);
        Assert.That(answers.Current[0].Name, Is.EqualTo(renamed));
    }

    static Task<bool> Next<T>(IAsyncEnumerator<T> answers) =>
        answers.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
}
