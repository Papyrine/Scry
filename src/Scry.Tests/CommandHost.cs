using Microsoft.EntityFrameworkCore;

// RenameShift is deprecated in the test model, to exercise a deprecated command; its handler still
// has to name it.
#pragma warning disable CS0618

/// <summary>
/// A processor with commands on, over a database of its own, with the test model's three commands
/// handled in-process by handlers a <see cref="CommandScript"/> steers: hold one open, make one throw,
/// make one skip saving or write in bulk. The context handlers write through carries the change
/// interceptor, so a live query on the same processor hears what a command saved.
/// </summary>
sealed class CommandHost :
    IAsyncDisposable
{
    CommandHost(SqlDatabase<TestContext> database, ScryProcessor processor, ServiceProvider services, CommandScript script)
    {
        Database = database;
        Processor = processor;
        Services = services;
        Script = script;
    }

    public SqlDatabase<TestContext> Database { get; }

    public ScryProcessor Processor { get; }

    public ServiceProvider Services { get; }

    public CommandScript Script { get; }

    public List<ScryAuditEntry> Audited => Services.GetRequiredService<RecordingAuditor>().Entries;

    public static async Task<CommandHost> Start(
        string name,
        Action<ScryOptions>? configure = null,
        Action<IServiceCollection>? register = null)
    {
        var database = await TestContext.CreateIsolated(name);
        await using (var seeding = database.NewDbContext())
        {
            seeding.Shifts.Add(
                new()
                {
                    Name = "Early",
                    Day = new(2026, 3, 4)
                });
            seeding.Contracts.AddRange(
                new()
                {
                    Id = 1,
                    Name = "Lease"
                },
                new()
                {
                    Id = UnsealedContractsPolicy.SealedId,
                    Name = "Sealed"
                });
            await seeding.SaveChangesAsync();
        }

        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.MaxPendingCommands = 100;
            options.CommandSyncWindow = TimeSpan.FromSeconds(5);
            options.MaxSubscriptions = 10;
            options.SubscriptionThrottle = TimeSpan.Zero;
            options.SubscriptionPollInterval = null;
            configure?.Invoke(options);
        });

        var contextOptions = new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer(database.ConnectionString)
            .AddInterceptors(new ScryChangeInterceptor(processor.Changes))
            .Options;

        // What EnsureReady does on a host: without a model a reported type cannot be traced to the
        // entity its rows are read through, and is reported as anything having changed.
        await using (var modelled = database.NewDbContext())
        {
            processor.Changes.Attach(modelled.Model);
        }
        var script = new CommandScript();
        var collection = new ServiceCollection()
            .AddSingleton(script)
            .AddSingleton<RecordingAuditor>()
            .AddSingleton<IScryAuditor>(_ => _.GetRequiredService<RecordingAuditor>())
            .AddScoped(_ => new TestContext(contextOptions))
            .AddScoped<ICommandHandler<Seal>, SealHandler>()
            .AddScoped<ICommandHandler<RenameShift>, RenameShiftHandler>()
            .AddScoped<ICommandHandler<CreateShift, ShiftCreated>, CreateShiftHandler>();
        register?.Invoke(collection);
        return new(database, processor, collection.BuildServiceProvider(), script);
    }

    /// <summary>Sends a command as <paramref name="caller"/>, reading every receipt it is answered with.</summary>
    public async Task<List<CommandReceipt>> Send(
        string command,
        object payload,
        string? caller = null,
        Guid? id = null,
        IServiceProvider? services = null,
        Cancel cancel = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var provider = services ?? scope.ServiceProvider;
        await using var reading = Database.NewDbContext();
        List<CommandReceipt> receipts = [];
        await foreach (var receipt in Processor.SendCommand(Request(command, payload, id), reading, provider, new HeaderDictionary(), caller, cancel))
        {
            receipts.Add(receipt);
        }

        return receipts;
    }

    public static CommandRequest Request(string command, object payload, Guid? id = null) =>
        CommandRequest.Create(command, id ?? Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, ScryJson.Options));

    public string ShiftName()
    {
        using var context = Database.NewDbContext();
        return context.Shifts.Single().Name;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await Database.DisposeAsync();
    }
}

/// <summary>What the test handlers do, set by a test before it sends.</summary>
sealed class CommandScript
{
    /// <summary>Held open until a test completes it, which is what makes a command pending.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public Exception? Throw { get; set; }

    public bool SkipSaving { get; set; }

    /// <summary>Renames with ExecuteUpdate, which no interceptor sees.</summary>
    public bool Bulk { get; set; }

    public ConcurrentQueue<(string Command, string? Caller, IReadOnlyList<object> Keys)> Seen { get; } = new();

    public async Task Run(string command, ScryCommandContext context, Cancel cancel)
    {
        Seen.Enqueue((command, context.Caller, context.TargetKeys));
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancel);
        }

        if (Throw is { } exception)
        {
            throw exception;
        }

        if (SkipSaving)
        {
            context.SaveChanges = false;
        }
    }
}

sealed class SealHandler(CommandScript script) :
    ICommandHandler<Seal>
{
    public Task Handle(Seal command, ScryCommandContext context, Cancel cancel) =>
        script.Run("SealContract", context, cancel);
}

sealed class RenameShiftHandler(CommandScript script, TestContext data) :
    ICommandHandler<RenameShift>
{
    public async Task Handle(RenameShift command, ScryCommandContext context, Cancel cancel)
    {
        await script.Run("RenameShift", context, cancel);
        if (script.Bulk)
        {
            await data.Shifts
                .Where(_ => _.Id == command.Id)
                .ExecuteUpdateAsync(_ => _.SetProperty(shift => shift.Name, command.Name), cancel);
            return;
        }

        data.Shifts.Single(_ => _.Id == command.Id).Name = command.Name;
    }
}

sealed class CreateShiftHandler(CommandScript script, TestContext data) :
    ICommandHandler<CreateShift, ShiftCreated>
{
    public async Task<ShiftCreated> Handle(CreateShift command, ScryCommandContext context, Cancel cancel)
    {
        await script.Run("CreateShift", context, cancel);
        var shift = new Shift
        {
            Name = command.Name,
            Day = command.Day
        };
        data.Shifts.Add(shift);
        await data.SaveChangesAsync(cancel);
        return new()
        {
            Id = shift.Id
        };
    }
}

sealed class RecordingAuditor :
    IScryAuditor
{
    public List<ScryAuditEntry> Entries { get; } = [];

    public void Record(ScryAuditEntry entry)
    {
        lock (Entries)
        {
            Entries.Add(entry);
        }
    }
}
