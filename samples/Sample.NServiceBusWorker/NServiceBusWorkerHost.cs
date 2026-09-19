/// <summary>
/// An endpoint that writes the data a Scry server reads and serves no queries itself. It maps no
/// route and registers no <c>AddScry</c>: all it has of Scry is the means to say what it saved.
/// </summary>
public static class NServiceBusWorkerHost
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var database = Database(args);

        // begin-snippet: sampleNServiceBusWorker
        // Change reporting on its own, and NServiceBus as what carries it to the servers.
        builder.Services.AddScryNServiceBusBackplane();

        // The interceptor is what knows which entities a save touched. It reports to the registration
        // above, which is why it is resolved rather than constructed.
        builder.Services.AddDbContext<SampleContext>(
            (services, options) => options
                .UseSqlServer(database)
                .AddInterceptors(services.GetRequiredService<ScryChangeInterceptor>()));

        // What each message's handlers saved is published once they are done, through that message's
        // own context — so it leaves only if the handler's work was kept.
        var endpoint = NServiceBusEndpoint.Create("Sample.Worker", args);
        endpoint.UseScryChanges();
        builder.Services.AddNServiceBusEndpoint(endpoint);
        // end-snippet

        return builder.Build();
    }

    static string Database(string[] args)
    {
        var index = Array.IndexOf(args, "--database");
        if (index >= 0 &&
            index + 1 < args.Length)
        {
            return args[index + 1];
        }

        throw new("Pass --database with the connection string Sample.NServiceBusServer printed when it started.");
    }
}

// An ordinary handler. Nothing in it mentions Scry: it saves, and the save is what gets reported.
// begin-snippet: sampleNServiceBusHandler
public sealed class RepriceOrderHandler(SampleContext data) :
    IHandleMessages<RepriceOrder>
{
    public async Task Handle(RepriceOrder message, IMessageHandlerContext context)
    {
        var order = await data.Orders.FindAsync([message.Id], context.CancellationToken);
        if (order is null)
        {
            return;
        }

        order.Amount += 1;
        await data.SaveChangesAsync(context.CancellationToken);
    }
}
// end-snippet
