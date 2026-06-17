using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scry.Wire;

namespace Scry;

/// <summary>Registration and endpoint wiring for the Scry server.</summary>
public static class ScryServiceExtensions
{
    /// <summary>Registers the query executor and builds the allow-list schema from the model.</summary>
    public static IServiceCollection AddScry(this IServiceCollection services, Action<ScryOptions> configure)
    {
        var options = new ScryOptions();
        configure(options);

        var schema = ScrySchema.Build(options);
        services.AddSingleton(options);
        services.AddSingleton(new ScryProcessor(schema, options));
        return services;
    }

    /// <summary>Maps the query-execution endpoint (HTTP POST) at <paramref name="pattern"/>.</summary>
    public static IEndpointConventionBuilder MapScry(this IEndpointRouteBuilder endpoints, string pattern) =>
        endpoints.MapPost(pattern, Handle);

    static async Task Handle(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        try
        {
            var request = ScryJson.DeserializeRequest(body);
            var db = (DbContext)services.GetRequiredService(options.ContextType!);
            var response = processor.Execute(request, db, services);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ScryJson.Serialize(response), context.RequestAborted);
        }
        catch (ScryWireException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (ScryValidationException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (Exception)
        {
            // Never leak internals (stack traces, SQL) to the client.
            await WriteError(context, StatusCodes.Status500InternalServerError, "Query execution failed.");
        }
    }

    static async Task WriteError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ScryError(message), context.RequestAborted);
    }

    sealed record ScryError(string Error);
}
