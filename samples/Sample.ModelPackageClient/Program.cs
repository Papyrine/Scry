/// <summary>
/// The console client's first query, from a client generated against Sample.Model as a NuGet package.
/// Nothing here differs from a client generated against the model project: only where the generator
/// found the model's DLL does.
/// </summary>
class Program
{
    /// <summary>Where Sample.WebServer listens, per its launchSettings.json.</summary>
    const string serverAddress = "http://localhost:5000";

    /// <param name="args"><c>--server &lt;url&gt;</c> points at a server other than Sample.WebServer.</param>
    static async Task<int> Main(string[] args)
    {
        var index = Array.IndexOf(args, "--server");
        var server = index >= 0 && index + 1 < args.Length ? args[index + 1] : serverAddress;

        using var http = new HttpClient
        {
            BaseAddress = new(server)
        };
        var query = new ScryQuery(ScryClient.ForHttp(http, "/api/query"));

        try
        {
            var rows = await query
                .Employee
                .Where(_ => _.Active)
                .OrderBy(_ => _.Name)
                .Select(_ => new EmployeeRow(_.Name, _.Status, _.Department!.Name))
                .ToListAsync();

            foreach (var row in rows)
            {
                Console.WriteLine($"{row.Name}  {row.Status}  {row.Department}");
            }

            return 0;
        }
        catch (HttpRequestException exception)
        {
            await Console.Error.WriteLineAsync($"Cannot reach {server}: {exception.Message}");
            await Console.Error.WriteLineAsync("Start the server with: dotnet run --project samples/Sample.WebServer");
            return 1;
        }
    }

    record EmployeeRow(string Name, Status Status, string Department);
}
