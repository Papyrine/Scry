class Program
{
    /// <summary>Where Sample.WebServer listens, per its launchSettings.json.</summary>
    const string serverAddress = "http://localhost:5000";

    /// <param name="args">
    /// <c>--live</c> watches the orders instead of printing the tables and exiting.
    /// <c>--server &lt;url&gt;</c> points at a server other than Sample.WebServer — one of the
    /// backplane samples, which serve queries and nothing else.
    /// </param>
    static async Task<int> Main(string[] args)
    {
        var server = Value(args, "--server") ?? serverAddress;

        // A console app needs no container and no host. An HttpClient, the endpoint MapScry was given,
        // and the generated entry point are the whole of it.
        // begin-snippet: consoleClientSetup
        using var http = new HttpClient
        {
            BaseAddress = new(server)
        };
        var query = new ScryQuery(ScryClient.ForHttp(http, "/api/query"));
        // end-snippet

        try
        {
            if (args.Contains("--live"))
            {
                await WatchOrders(query);
                return 0;
            }

            await ShowActiveEmployees(query);
            await ShowRegions(query);
            await ShowActiveCount(query);
            return 0;
        }
        catch (HttpRequestException exception)
        {
            await Console.Error.WriteLineAsync($"Cannot reach {server}: {exception.Message}");
            await Console.Error.WriteLineAsync("Start the server with: dotnet run --project samples/Sample.WebServer");
            return 1;
        }
    }

    static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 ||
            index + 1 >= args.Length)
        {
            return null;
        }

        return args[index + 1];
    }

    record OrderRow(int Id, string Region, decimal Amount);

    // A live query is an IAsyncEnumerable, so it is read the way any other is. Each answer is the
    // whole current result, and the loop runs until Ctrl+C cancels it — which is also what tells the
    // server the subscription is over.
    // begin-snippet: consoleLive
    static async Task WatchOrders(ScryQuery query)
    {
        using var leaving = new CancellationTokenSource();
        Console.CancelKeyPress += (_, pressed) =>
        {
            pressed.Cancel = true;
            leaving.Cancel();
        };

        Console.WriteLine("Watching orders. Ctrl+C to stop.");
        Console.WriteLine();
        var orders = query
            .Order
            .OrderBy(_ => _.Id)
            .Select(_ => new OrderRow(_.Id, _.Region, _.Amount))
            .Live();

        try
        {
            await foreach (var answer in orders.WithCancellation(leaving.Token))
            {
                Console.WriteLine($"{DateTime.Now:T}");
                WriteTable(
                    ["Id", "Region", "Amount"],
                    [.. answer.Select(_ => new[] {_.Id.ToString(), _.Region, _.Amount.ToString("0.00")})]);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }
    }
    // end-snippet

    // The shapes this app wants, declared here rather than anywhere the server knows about. The
    // response comes back keyed by these names.
    record EmployeeRow(string Name, Status Status, string? Manager, string Department);

    record RegionSummary(string Region, decimal Total, int Count);

    // A filter, an ordering, and a projection that reaches through two navigations. The LINQ is
    // captured rather than executed: it is translated to the wire AST, validated against the
    // allow-list on the server, rebound to the real Employee, and run through EF Core.
    // begin-snippet: consoleQuery
    static async Task ShowActiveEmployees(ScryQuery query)
    {
        var rows = await query
            .Employee
            .Where(_ => _.Active)
            .OrderBy(_ => _.Name)
            .Select(_ => new EmployeeRow(_.Name, _.Status, _.Manager!.Name, _.Department!.Name))
            .ToListAsync();

        WriteTable(
            ["Name", "Status", "Manager", "Department"],
            [.. rows.Select(_ => new[] {_.Name, _.Status.ToString(), _.Manager ?? "", _.Department})]);
    }
    // end-snippet

    // Grouping and aggregation happen on the server, so what crosses the wire is one row per region
    // rather than every order.
    // begin-snippet: consoleGroupBy
    static async Task ShowRegions(ScryQuery query)
    {
        var rows = await query
            .Order
            .GroupBy(_ => _.Region)
            .Select(_ => new RegionSummary(_.Key, _.Sum(order => order.Amount), _.Count()))
            .ToListAsync();

        WriteTable(
            ["Region", "Total", "Orders"],
            [.. rows.Select(_ => new[] {_.Region, _.Total.ToString("0.00"), _.Count.ToString()})]);
    }
    // end-snippet

    // A terminal that takes a predicate of its own translates it the same way.
    // begin-snippet: consoleCount
    static async Task ShowActiveCount(ScryQuery query)
    {
        var active = await query.Employee.CountAsync(_ => _.Active);
        Console.WriteLine($"{active} active employees");
        Console.WriteLine();
    }
    // end-snippet

    // Nothing to do with Scry: enough column alignment to make the rows readable in a terminal.
    static void WriteTable(string[] headers, IReadOnlyList<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (var column = 0; column < headers.Length; column++)
        {
            widths[column] = headers[column].Length;
            foreach (var row in rows)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
            }
        }

        Console.WriteLine(string.Join("  ", headers.Select((_, column) => _.PadRight(widths[column]))));
        Console.WriteLine(string.Join("  ", widths.Select(_ => new string('-', _))));
        foreach (var row in rows)
        {
            Console.WriteLine(string.Join("  ", row.Select((_, column) => _.PadRight(widths[column]))));
        }

        Console.WriteLine();
    }
}
