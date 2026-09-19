/// <summary>
/// A Scry server that hears, over NServiceBus, what other processes wrote. The write this sample
/// exists to show is not made here at all: the server sends a command, a worker in another process
/// handles it and saves, and the live queries held here are asked again because the worker said so.
/// </summary>
public static class NServiceBusServerHost
{
    public static async Task<WebApplication> Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var database = await BackplaneHost.Database(args, "Sample.NServiceBusServer", "Sample.NServiceBusWorker");
        BackplaneHost.AddData(builder.Services, database);

        // begin-snippet: sampleNServiceBusBackplane
        builder.Services.AddScry<SampleContext>(
            _ =>
            {
                BackplaneHost.Configure(_);

                // Hears the ScryChanged events other endpoints publish, and publishes this node's own
                // saves as one. The endpoint below is what it hears them through.
                _.UseNServiceBusBackplane();
            });

        // An endpoint of its own for each node. NServiceBus hands an event to one instance of each
        // endpoint, so nodes sharing a name would share the changes out between them rather than
        // each hearing all of them. And a full endpoint rather than a send-only one, which could
        // send the command below and would hear nothing back.
        var endpoint = NServiceBusEndpoint.Create($"Sample.Web.{Port(args)}", args);
        builder.Services.AddNServiceBusEndpoint(endpoint);
        // end-snippet

        var app = builder.Build();
        BackplaneHost.Map(app);

        // Nothing is written here. The command goes to the worker, and what comes back is not a
        // reply: it is the worker saying, to anyone listening, that orders changed.
        app.MapPost(
            "/api/orders/{id:int}/reprice-via-worker",
            async (int id, IMessageSession session) =>
            {
                await session.Send(
                    "Sample.Worker",
                    new RepriceOrder
                    {
                        Id = id
                    });
                return Results.Accepted();
            });
        return app;
    }

    // The port the node was asked to listen on, which is what tells two nodes on one machine apart.
    static int Port(string[] args)
    {
        var index = Array.IndexOf(args, "--urls");
        if (index >= 0 &&
            index + 1 < args.Length &&
            Uri.TryCreate(args[index + 1].Split(';')[0], UriKind.Absolute, out var url))
        {
            return url.Port;
        }

        return 5101;
    }
}
