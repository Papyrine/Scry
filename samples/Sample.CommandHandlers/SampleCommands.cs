namespace Sample.CommandHandlers;

/// <summary>What the sample's commands are tuned by: bound from <c>Sample:Commands</c>.</summary>
public sealed class SampleCommandOptions
{
    /// <summary>How long a rename to a name containing "slow" takes: long enough to go pending.</summary>
    public TimeSpan SlowDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether anyone may hire. The one command-wide decision in the sample that can say no, so that a
    /// capability reading false can be seen somewhere.
    /// </summary>
    public bool AllowCreate { get; set; } = true;
}

/// <summary>The sample's command registration, split as a host's is: services on one side, options on the other.</summary>
public static class SampleCommands
{
    // begin-snippet: commandRegistration
    /// <summary>The handlers every sample command needs, and the options they read.</summary>
    public static IServiceCollection AddSampleCommandHandlers(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = services.AddOptions<SampleCommandOptions>();
        if (configuration is not null)
        {
            options.Bind(configuration.GetSection("Sample:Commands"));
        }

        services.AddScoped<ICommandHandler<DeleteEmployee>, DeleteEmployeeHandler>();
        services.AddScoped<ICommandHandler<RenameEmployee>, RenameEmployeeHandler>();
        services.AddScoped<ICommandHandler<SetEmployeeActive>, SetEmployeeActiveHandler>();
        services.AddScoped<ICommandHandler<CreateEmployee, EmployeeCreated>, CreateEmployeeHandler>();
        services.AddScoped<ICommandHandler<RepriceOrder>, RepriceOrderHandler>();

        // A policy with dependencies is resolved rather than constructed, so it is registered.
        services.AddScoped<CreateEmployeePolicy>();
        return services;
    }

    /// <summary>Commands on, answered at once where they finish within a second, and their policies.</summary>
    public static ScryOptions UseSampleCommands(this ScryOptions options)
    {
        // Off until a server says how many it will have in flight at once, which is also what maps
        // the routes — as MaxSubscriptions is for live queries.
        options.MaxPendingCommands = 100;
        options.CommandSyncWindow = TimeSpan.FromSeconds(1);
        options.AddCommandPolicy<DeleteEmployee, DeleteEmployeePolicy>();
        options.AddCommandPolicy<CreateEmployee, CreateEmployeePolicy>();
        return options;
    }
    // end-snippet
}
