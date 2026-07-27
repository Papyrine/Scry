namespace Scry;

/// <summary>Registration and endpoint wiring for the Scry server.</summary>
public static class ScryServiceExtensions
{
    /// <summary>Registers the query executor and builds the allow-list schema from the model.</summary>
    public static IServiceCollection AddScry<TContext>(this IServiceCollection services, Action<ScryOptions> configure)
        where TContext : DbContext
    {
        var options = new ScryOptions(typeof(TContext));
        configure(options);

        var schema = Schema.Build(options);
        services.AddSingleton(options);
        services.AddSingleton(new ScryProcessor(schema, options));
        return services;
    }

    /// <summary>Maps the query-execution endpoint (HTTP POST) at <paramref name="pattern"/>.</summary>
    public static IEndpointConventionBuilder MapScry(this IEndpointRouteBuilder endpoints, string pattern)
    {
        // Validate the annotations against the live EF model once, at startup, where a model exists.
        // A misapplied [Queryable]/[QueryableComplex] fails loudly here rather than obscurely per-query.
        using (var scope = endpoints.ServiceProvider.CreateScope())
        {
            var options = scope.ServiceProvider.GetRequiredService<ScryOptions>();
            var processor = scope.ServiceProvider.GetRequiredService<ScryProcessor>();
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);
            processor.ValidateAgainstModel(db);
        }

        return endpoints.MapPost(pattern, Handle);
    }

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
            var db = (DbContext)services.GetRequiredService(options.ContextType);
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

    static Task WriteError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new ScryError(message), context.RequestAborted);
    }

    // ReSharper disable once NotAccessedPositionalProperty.Local
    sealed record ScryError(string Error);
}
