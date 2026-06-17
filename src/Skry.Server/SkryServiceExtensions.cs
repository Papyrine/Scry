using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skry.Wire;

namespace Skry;

/// <summary>Registration and endpoint wiring for the Skry server.</summary>
public static class SkryServiceExtensions
{
    /// <summary>Registers the query executor and builds the allow-list schema from the model.</summary>
    public static IServiceCollection AddSkry(this IServiceCollection services, Action<SkryOptions> configure)
    {
        var options = new SkryOptions();
        configure(options);

        var schema = SkrySchema.Build(options);
        services.AddSingleton(options);
        services.AddSingleton(new SkryProcessor(schema, options));
        return services;
    }

    /// <summary>Maps the query-execution endpoint (HTTP POST) at <paramref name="pattern"/>.</summary>
    public static IEndpointConventionBuilder MapSkry(this IEndpointRouteBuilder endpoints, string pattern) =>
        endpoints.MapPost(pattern, Handle);

    static async Task Handle(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<SkryOptions>();
        var processor = services.GetRequiredService<SkryProcessor>();

        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        try
        {
            var request = SkryJson.DeserializeRequest(body);
            var db = (DbContext)services.GetRequiredService(options.ContextType!);
            var response = processor.Execute(request, db, services);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(SkryJson.Serialize(response), context.RequestAborted);
        }
        catch (SkryWireException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (SkryValidationException exception)
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
        await context.Response.WriteAsJsonAsync(new SkryError(message), context.RequestAborted);
    }

    sealed record SkryError(string Error);
}
