namespace Scry;

/// <summary>Wiring for serving Scry to AI agents over MCP.</summary>
public static class ScryMcpExtensions
{
    // begin-snippet: addScryMcp
    /// <summary>
    /// Registers the MCP server <c>MapScryMcp</c> maps, serving the tools
    /// <see cref="ScryOptions.Mcp"/> allows. Needs <c>AddScry</c>.
    /// </summary>
    /// <remarks>
    /// The server is stateless: each tool call is an HTTP request of its own, answered in that request's
    /// scope, so a row policy reads the caller exactly as it does for <c>MapScry</c>.
    /// </remarks>
    public static IServiceCollection AddScryMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfigureOptions<McpServerOptions>, ConfigureScryMcp>();
        services
            .AddMcpServer()
            .WithHttpTransport(_ => _.Stateless = true);
        return services;
    }
    // end-snippet

    // begin-snippet: mapScryMcp
    /// <summary>
    /// Maps the MCP endpoint at <paramref name="pattern"/>, where <see cref="ScryOptions.Mcp"/> is not
    /// <see cref="ScryMcpAccess.Off"/>. Off, it maps nothing: a server serves agents because it said it
    /// would.
    /// </summary>
    /// <remarks>
    /// Runs the same startup checks <c>MapScry</c> does. Authorization goes on what this returns, and
    /// applies to every tool; a row policy then narrows what that caller sees, as it does over HTTP.
    /// </remarks>
    public static IEndpointConventionBuilder MapScryMcp(this IEndpointRouteBuilder endpoints, string pattern)
    {
        var services = endpoints.ServiceProvider;
        var options = services.GetRequiredService<ScryOptions>();
        services
            .GetRequiredService<ScryProcessor>()
            .EnsureReady(services);
        if (options.Mcp == ScryMcpAccess.Off)
        {
            return new NoEndpoints();
        }

        EnsureCommandsServed(options);
        return endpoints.MapMcp(pattern);
    }
    // end-snippet

    // An agent told it may write, by a server that would refuse every write, is a host misconfigured.
    static void EnsureCommandsServed(ScryOptions options)
    {
        if (options.Mcp != ScryMcpAccess.ReadWrite ||
            options.MaxPendingCommands > 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"ScryOptions.{nameof(ScryOptions.Mcp)} is {nameof(ScryMcpAccess.ReadWrite)}, but this server serves no commands: set ScryOptions.{nameof(ScryOptions.MaxPendingCommands)}, or serve agents {nameof(ScryMcpAccess.Read)}.");
    }

    sealed class ConfigureScryMcp(ScryOptions scry) :
        IConfigureOptions<McpServerOptions>
    {
        public void Configure(McpServerOptions options)
        {
            options.ServerInfo = new()
            {
                Name = "Scry",
                Version = typeof(ScryMcpExtensions).Assembly.GetName().Version?.ToString() ?? "0"
            };
            options.ServerInstructions = ScryMcpGrammar.Instructions(scry.Mcp);
            options.ToolCollection ??= [];
            foreach (var tool in ScryMcpTools.For(scry.Mcp))
            {
                options.ToolCollection.Add(tool);
            }
        }
    }

    // What an unmapped route answers conventions with: nothing to apply them to.
    sealed class NoEndpoints :
        IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention)
        {
        }
    }
}
