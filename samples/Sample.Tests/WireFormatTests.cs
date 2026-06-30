// Captures the actual HTTP traffic between client and server and snapshots it. This documents
// Scry's wire format: the serialized LINQ query that goes up, and the projected rows that come back.
[TestFixture]
public class WireFormatTests
{
    public record EmployeeRow(string Name, Status Status, string? Manager, string Department);

    [Test]
    public async Task EmployeeQueryWireFormat()
    {
        await using var server = await ScryTestServer.StartAsync();

        var services = new ServiceCollection();
        var httpBuilder = services
            .AddHttpClient(
                "scry",
                _ => _.BaseAddress = new("http://localhost/"))
            .ConfigurePrimaryHttpMessageHandler(server.CreateHandler);
        var recording = httpBuilder.AddRecording();

        await using var provider = services.BuildServiceProvider();
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("scry");
        var query = new ScryQuery(ScryClient.ForHttp(http, "/api/query"));

        await query.Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
            .ToListAsync();

        await Verify(recording.Sends);
    }
}
