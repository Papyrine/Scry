namespace Scry;

public static partial class ScryServiceExtensions
{
    /// <summary>
    /// Sends a command, answering with its outcome where it finished within the sync window and with a
    /// stream of receipts where it did not: pending at once, then the outcome when it lands.
    /// </summary>
    /// <remarks>
    /// Everything a command can be refused for is decided before the response is committed — malformed,
    /// unknown, denied, its target not there for the caller, one command too many — so each is an
    /// ordinary status with an ordinary body, and nothing is dispatched for any of them.
    /// </remarks>
    static async Task HandleCommand(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        Advertise(context, processor, options);
        if (!await RequireJson(context))
        {
            return;
        }

        // A declared length over the limit is refused before a byte is read, and one that lies is refused
        // once what arrived passes it.
        if (context.Request.ContentLength > options.MaxCommandBytes ||
            await ReadBoundedBody(context, options.MaxCommandBytes) is not { } body)
        {
            await WriteError(
                context,
                StatusCodes.Status413PayloadTooLarge,
                $"A command body may be at most {options.MaxCommandBytes} bytes.",
                ScryErrorCode.PayloadTooLarge);
            return;
        }

        CommandRequest request;
        try
        {
            request = ScryJson.DeserializeCommandRequest(body);
        }
        catch (ScryWireException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, ScryErrorCode.WireFormat);
            return;
        }

        var drifted = request.Stamp is { } stamp && stamp != processor.SchemaStamp;
        var db = (DbContext) services.GetRequiredService(options.ContextType);
        using var ending = CancelSource.CreateLinkedTokenSource(context.RequestAborted);
        await using var receipts = new HeldItems<CommandReceipt>(
            processor.SendCommand(request, db, services, context.Request.Headers, options.Caller(context), ending.Token),
            ending);
        await AnswerCommand(context, options, receipts, drifted);
    }

    /// <summary>
    /// A command already sent, asked for again by its id: its outcome, or — while it is still in flight —
    /// the same stream the command itself was answered with. Only for the caller that sent it.
    /// </summary>
    static async Task HandleReceipt(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        Advertise(context, processor, options);
        var id = Guid.Parse((string) context.Request.RouteValues["id"]!, CultureInfo.InvariantCulture);
        using var ending = CancelSource.CreateLinkedTokenSource(context.RequestAborted);
        await using var receipts = new HeldItems<CommandReceipt>(
            processor.Receipt(id, options.Caller(context), ending.Token),
            ending);
        await AnswerCommand(context, options, receipts, drifted: false);
    }

    /// <summary>The commands this caller may send at all, for a UI to enable its controls by.</summary>
    static Task HandleCapabilities(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<ScryOptions>();
        var processor = services.GetRequiredService<ScryProcessor>();

        Advertise(context, processor, options);
        var db = (DbContext) services.GetRequiredService(options.ContextType);
        return WriteCommandJson(context, ScryJson.SerializeToUtf8(processor.Capabilities(db, services, context.Request.Headers)));
    }

    // A final first receipt is a response of its own; a pending one is the first event of a stream that
    // closes after the outcome.
    static async Task AnswerCommand(HttpContext context, ScryOptions options, HeldItems<CommandReceipt> receipts, bool drifted)
    {
        if (!await FirstCommandReceipt(context, receipts, drifted))
        {
            return;
        }

        var first = receipts.Current;
        if (first.Status != CommandStatus.Pending)
        {
            await WriteCommandJson(context, ScryJson.SerializeToUtf8(first));
            return;
        }

        Commit(context);
        await WriteEvent(context, ScryLive.Result, id: null, ScryJson.SerializeToUtf8(first));
        receipts.Advance();
        await Hold(
            context,
            options,
            () => receipts.Pending,
            async () =>
            {
                try
                {
                    if (!await receipts.Pending)
                    {
                        return false;
                    }
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception)
                {
                    await Fail(context, "Command dispatch failed.", ScryErrorCode.ExecutionFailed);
                    return false;
                }

                // The outcome: the stream has said all it will.
                await WriteEvent(context, ScryLive.Result, id: null, ScryJson.SerializeToUtf8(receipts.Current));
                return false;
            });
    }

    // The first receipt, or the status that says why there is not one. True when there is.
    static async Task<bool> FirstCommandReceipt(HttpContext context, HeldItems<CommandReceipt> receipts, bool drifted)
    {
        try
        {
            return await receipts.Pending;
        }
        catch (ScryValidationException exception)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, exception.Message, ErrorCodes.Classify(exception));
        }
        catch (ScryPermissionException exception)
        {
            await WriteError(context, StatusCodes.Status403Forbidden, exception.Message, ScryErrorCode.Forbidden);
        }
        catch (ScryCommandNotFoundException exception)
        {
            await WriteError(context, StatusCodes.Status404NotFound, exception.Message, ScryErrorCode.NotFound);
        }
        catch (ScryCommandLimitException exception)
        {
            // Nothing ran, so the same command is safe to send again — soon, since a command in flight
            // finishes in the time it takes to handle one, unlike a live query that holds its place.
            context.Response.Headers.RetryAfter = "1";
            await WriteError(
                context,
                exception.PerCaller ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable,
                exception.Message,
                ScryErrorCode.CommandLimit);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Nobody is left to answer.
        }
        catch (Exception)
        {
            await WriteError(context, StatusCodes.Status500InternalServerError, "Command dispatch failed.", ErrorCodes.Failed(drifted));
        }

        return false;
    }

    // Reads a body up to the limit and no further, answering null where it runs past it — a declared
    // length is a claim, and a chunked body declares none.
    static async Task<byte[]?> ReadBoundedBody(HttpContext context, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    // A receipt or a caller's capabilities: shaped by who asked, so never kept.
    static Task WriteCommandJson(HttpContext context, byte[] json)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        response.Headers.CacheControl = "no-store";
        return response.Body.WriteAsync(json, context.RequestAborted).AsTask();
    }
}
