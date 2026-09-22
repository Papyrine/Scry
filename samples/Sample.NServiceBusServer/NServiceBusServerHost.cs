/// <summary>
/// A Scry server that hears, over NServiceBus, what other processes wrote. The write this sample
/// exists to show is not made here at all: a client sends the <c>RepriceOrder</c> command, the server
/// sends it on to a worker in another process, the worker handles it and saves — and replies, which
/// finishes the command here, and publishes what it saved, which re-asks the live queries held here.
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

                // begin-snippet: sampleNServiceBusCommands
                // RepriceOrder goes to the worker rather than to the in-process handler BackplaneHost
                // registered: a dispatcher's claim comes first. The endpoint's routing says where it goes,
                // and the worker's reply to this endpoint is what finishes it.
                _.UseNServiceBusCommands(_ => _.For<RepriceOrder>());
                // end-snippet
            });

        // An endpoint of its own for each node. NServiceBus hands an event to one instance of each
        // endpoint, so nodes sharing a name would share the changes out between them rather than
        // each hearing all of them — and a worker's reply comes back to the node that sent the
        // command. A full endpoint rather than a send-only one, which could send commands and would
        // hear nothing back.
        var endpoint = NServiceBusEndpoint.Create($"Sample.Web.{Port(args)}", args);
        builder.Services.AddNServiceBusEndpoint(endpoint);
        // end-snippet

        var app = builder.Build();
        BackplaneHost.Map(app);
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
