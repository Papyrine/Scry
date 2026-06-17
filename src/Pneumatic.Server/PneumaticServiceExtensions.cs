using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pneumatic.Wire;

namespace Pneumatic;

/// <summary>Registration and endpoint wiring for the Pneumatic server.</summary>
public static class PneumaticServiceExtensions
{
    /// <summary>Registers the query executor and builds the allow-list schema from the model.</summary>
    public static IServiceCollection AddPneumatic(this IServiceCollection services, Action<PneumaticOptions> configure)
    {
        var options = new PneumaticOptions();
        configure(options);

        var schema = PneumaticSchema.Build(options);
        services.AddSingleton(options);
        services.AddSingleton(new PneumaticProcessor(schema, options));
        return services;
    }

    /// <summary>Maps the query-execution endpoint (HTTP POST) at <paramref name="pattern"/>.</summary>
    public static IEndpointConventionBuilder MapPneumatic(this IEndpointRouteBuilder endpoints, string pattern) =>
        endpoints.MapPost(pattern, Handle);

    static async Task Handle(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<PneumaticOptions>();
        var processor = services.GetRequiredService<PneumaticProcessor>();

        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        try
        {
            var request = PneumaticJson.DeserializeRequest(body);
            var db = (DbContext)services.GetRequiredService(options.ContextType!);
            var response = processor.Execute(request, db, services);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(PneumaticJson.Serialize(response), context.RequestAborted);
        }
        catch (PneumaticWireException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message);
        }
        catch (PneumaticValidationException exception)
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
        await context.Response.WriteAsJsonAsync(new PneumaticError(message), context.RequestAborted);
    }

    sealed record PneumaticError(string Error);
}
