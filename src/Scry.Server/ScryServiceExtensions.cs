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

        // Advertised on every response, including rejections, so a client can notice a drifted model
        // while its queries are still succeeding rather than only once one breaks. Set before any
        // write, since headers are fixed once the response has started.
        context.Response.Headers[WireFormat.SchemaStampHeader] = processor.SchemaStamp;

        string body;
        using (var reader = new StreamReader(context.Request.Body))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
        }

        QueryRequest request;
        try
        {
            request = ScryJson.DeserializeRequest(body);
        }
        catch (ScryWireException exception)
        {
            // A malformed request carries no usable stamp, so it is never attributed to staleness.
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, staleClient: false);
            return;
        }

        // A stamp that disagrees marks the client as generated against a different model surface.
        // Validation failures carry their own attribution (ScryValidationException.StaleClient); for
        // an execution failure this is the only attribution available — a drifted client faulting the
        // server (e.g. a constant parsed against a member whose type has since changed) is far more
        // likely stale than the server broken, and marking it lets the client prompt a reload instead
        // of presenting an unexplained server error.
        var drifted = request.Stamp is { } stamp && stamp != processor.SchemaStamp;

        try
        {
            var db = (DbContext)services.GetRequiredService(options.ContextType);
            var response = processor.Execute(request, db, services);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ScryJson.Serialize(response), context.RequestAborted);
        }
        catch (ScryValidationException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, exception.StaleClient);
        }
        catch (Exception)
        {
            // Never leak internals (stack traces, SQL) to the client.
            await WriteError(context, StatusCodes.Status500InternalServerError, "Query execution failed.", drifted);
        }
    }

    static Task WriteError(HttpContext context, int status, string message, bool staleClient)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(
            new ScryError(message) { StaleClient = staleClient },
            ScryJson.Options,
            context.RequestAborted);
    }
}
