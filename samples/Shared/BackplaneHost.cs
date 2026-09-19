using Microsoft.EntityFrameworkCore;

/// <summary>
/// What the backplane sample servers have in common, none of it to do with a backplane: one database
/// for two nodes, the Scry registration every one of them needs, and a write to watch arrive. Linked
/// into each so that each <c>Program.cs</c> is left holding only what it is there to show.
/// </summary>
/// <remarks>
/// A backplane is about several nodes, so these are run twice on different ports. The first node
/// builds the database and prints how to start the second, which attaches to it. Watch either with
/// <c>dotnet run --project samples/Sample.ConsoleClient -- --live --server &lt;url&gt;</c>, write to
/// the other, and the console reprints.
/// </remarks>
static class BackplaneHost
{
    // An instance of its own rather than the one Sample.WebServer and the test projects share: a
    // second process building the same template while another holds it is a deadlock inside SQL Server.
    static SqlInstance<SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            SampleContext.Initialize(_);
            return Task.CompletedTask;
        },
        storage: Storage.FromSuffix<SampleContext>("Backplane"));

    /// <summary>
    /// The connection string both nodes read and write through: the one passed as
    /// <c>--database</c>, or a database built now for a second node to be handed.
    /// </summary>
    /// <param name="worker">The project that writes to the same database from another process, where the sample has one.</param>
    public static async Task<string> Database(string[] args, string project, string? worker = null)
    {
        var index = Array.IndexOf(args, "--database");
        if (index >= 0 &&
            index + 1 < args.Length)
        {
            return args[index + 1];
        }

        var database = await sqlInstance.Build(project);
        Console.WriteLine();
        Console.WriteLine("This is the first node. Start a second one against the same database with:");
        Console.WriteLine($"  dotnet run --project samples/{project} -- --urls http://localhost:5102 --database \"{database.ConnectionString}\"");
        if (worker is not null)
        {
            Console.WriteLine("and the worker with:");
            Console.WriteLine($"  dotnet run --project samples/{worker} -- --database \"{database.ConnectionString}\"");
        }

        Console.WriteLine();
        return database.ConnectionString;
    }

    /// <summary>
    /// The context, with the interceptor that reports what it saves. What is reported reaches this
    /// node's live queries directly and every other node's through the backplane.
    /// </summary>
    public static void AddData(IServiceCollection services, string connectionString) =>
        services.AddDbContext<SampleContext>(
            (provider, options) => options
                .UseSqlServer(connectionString)
                .AddInterceptors(provider.GetRequiredService<ScryChangeInterceptor>()));

    /// <summary>What the model needs before a server will start, and live queries turned on.</summary>
    public static void Configure(ScryOptions options)
    {
        options.AddPocoSource(_ => Holiday.Seed());
        options.AddAttachmentPolicy<Department, AllowHandbook>();
        options.AddAttachmentPolicy<Employee, AllowPhoto>();
        options.MaxSubscriptions = 100;

        // Off, so that what reaches the other node can only have come over the backplane. A real
        // deployment leaves the poll on: it is what covers a message the backplane dropped.
        options.SubscriptionPollInterval = null;
    }

    /// <summary>The query endpoints, and one write to make on the node that is not being watched.</summary>
    public static void Map(WebApplication app)
    {
        app.MapScry("/api/query");
        app.MapPost("/api/orders/{id:int}/reprice", async (int id, SampleContext data) =>
        {
            var order = await data.Orders.FindAsync(id);
            if (order is null)
            {
                return Results.NotFound();
            }

            order.Amount += 1;
            await data.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    sealed class AllowHandbook :
        IAttachmentPolicy<Department>
    {
        public bool Authorize(ScryAttachmentContext context) => true;
    }

    sealed class AllowPhoto :
        IAttachmentPolicy<Employee>
    {
        public bool Authorize(ScryAttachmentContext context) => true;
    }
}
