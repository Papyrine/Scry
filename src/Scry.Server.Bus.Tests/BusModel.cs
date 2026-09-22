using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;

/// <summary>
/// The Scry server every bus fixture hosts: commands over a context whose model is all it is ever
/// asked for, since each command here is untargeted.
/// </summary>
static class ScryServer
{
    public static void Add(IServiceCollection services, TimeSpan? window, Action<ScryOptions> dispatch, bool second = false)
    {
        services.AddDbContext<BusContext>(_ => _.UseSqlServer("Server=.;Database=NeverOpened"));
        services.AddScry<BusContext>(options =>
        {
            options.AllowUnmappedSources = true;
            options.MaxPendingCommands = 10;
            options.CommandSyncWindow = window ?? TimeSpan.FromSeconds(20);
            dispatch(options);
            if (second)
            {
                options.AddDispatcher<ClaimsEverything>();
            }
        });
        services.AddSingleton<ClaimsEverything>();
        services.AddScoped<ICommandHandler<LocalChore>, LocalChoreHandler>();
    }

    public static async Task<List<CommandReceipt>> Send(IServiceProvider root, string command, object payload, Guid? id = null, string? caller = null)
    {
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var request = CommandRequest.Create(command, id ?? Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, ScryJson.Options));
        List<CommandReceipt> receipts = [];
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var receipt in services
                           .GetRequiredService<ScryProcessor>()
                           .SendCommand(request, services.GetRequiredService<BusContext>(), services, new HeaderDictionary(), caller, patience.Token))
        {
            receipts.Add(receipt);
        }

        return receipts;
    }

    public static void EnsureDispatchable(IServiceProvider root) =>
        root.GetRequiredService<ScryProcessor>().EnsureCommandsDispatchable(root);
}

/// <summary>What each bus's handler saw of the headers a command carried, by the label it was sent with.</summary>
static class Seen
{
    static ConcurrentDictionary<string, (string? CommandId, string? Caller)> headers = new();

    public static void Add(string label, string? commandId, string? caller) =>
        headers[label] = (commandId, caller);

    public static (string? CommandId, string? Caller) For(string label) =>
        headers[label];
}

sealed class ClaimsEverything :
    ICommandDispatcher
{
    public bool CanDispatch(Type command) => true;

    public Task Dispatch(CommandEnvelope envelope, CancellationToken cancel) =>
        Task.CompletedTask;
}

public sealed class BusContext(Microsoft.EntityFrameworkCore.DbContextOptions<BusContext> options) :
    Microsoft.EntityFrameworkCore.DbContext(options);

// The messages in a namespace of their own, unlike the rest of the tests: MassTransit refuses a
// message type that has none, since a message's address on the bus is built from it.
namespace Parcels
{
    /// <summary>A command handled over the bus: recorded, and thrown when told to.</summary>
    [Command]
    public sealed class ShipParcel
    {
        public string Label { get; set; } = "";
        public bool Fail { get; set; }
    }

    /// <summary>A command handled over the bus, answering with a result.</summary>
    [Command(Result = typeof(Weighed))]
    public sealed class WeighParcel
    {
        public int Grams { get; set; }
    }

    public sealed class Weighed
    {
        public int Grams { get; set; }
    }

    /// <summary>A command no bus claims: handled where the server is.</summary>
    [Command]
    public sealed class LocalChore;

    public sealed class LocalChoreHandler :
        ICommandHandler<LocalChore>
    {
        public Task Handle(LocalChore command, ScryCommandContext context, CancellationToken cancel) =>
            Task.CompletedTask;
    }
}
