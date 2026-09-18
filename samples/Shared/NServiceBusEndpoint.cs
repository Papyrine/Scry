using NServiceBus;
using Sample.Model;

/// <summary>
/// What the two NServiceBus samples configure the same way, none of it to do with Scry: the transport,
/// the serializer, and how a message is recognised. Linked into both so each <c>Program.cs</c> is left
/// holding what it is there to show.
/// </summary>
static class NServiceBusEndpoint
{
    /// <summary>
    /// The learning transport: files in a folder, so the sample needs nothing installed. Both
    /// endpoints have to be pointed at the same folder, which by default is one under the temporary
    /// directory and can be moved with <c>--transport-storage</c>.
    /// </summary>
    public static EndpointConfiguration Create(string name, string[] args)
    {
        var configuration = new EndpointConfiguration(name);
        configuration.UseSerialization<SystemJsonSerializer>();
        configuration.UseTransport(
            new LearningTransport
            {
                StorageDirectory = Storage(args)
            });
        configuration.SendFailedMessagesTo("Sample.Error");
        configuration.EnableInstallers();

        // By convention rather than by marker interface, so the message lives in the model assembly
        // without that assembly referencing NServiceBus.
        configuration.Conventions().DefiningCommandsAs(_ => _ == typeof(RepriceOrder));
        return configuration;
    }

    static string Storage(string[] args)
    {
        var index = Array.IndexOf(args, "--transport-storage");
        if (index >= 0 &&
            index + 1 < args.Length)
        {
            return args[index + 1];
        }

        return Path.Combine(Path.GetTempPath(), "scry-sample-learning-transport");
    }
}
