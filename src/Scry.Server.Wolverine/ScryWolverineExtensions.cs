namespace Scry;

/// <summary>Wires Wolverine up as what carries commands from Scry servers to the handlers that handle them.</summary>
public static class ScryWolverineExtensions
{
    // begin-snippet: useWolverineCommands
    /// <summary>
    /// On a Scry server: sends the commands <paramref name="configure"/> names over Wolverine, with
    /// their id and caller as headers and routed by the application's own routing, and finishes each
    /// when the response to it arrives. Claims nothing until told, since nothing about a Wolverine
    /// message says it is one.
    /// </summary>
    /// <remarks>
    /// The server's Wolverine options need <see cref="AddScryCommandCompletions"/>, which is what hears
    /// the responses.
    /// </remarks>
    public static ScryOptions UseWolverineCommands(this ScryOptions options, Action<BusCommands>? configure = null)
    {
        var claims = new BusCommands();
        configure?.Invoke(claims);
        options.AddDispatcher(_ => new WolverineDispatcher(_, claims));
        return options;
    }

    /// <summary>On the server's Wolverine options: the handler that hears how each command ended.</summary>
    public static WolverineOptions AddScryCommandCompletions(this WolverineOptions options)
    {
        options.Discovery.IncludeType(typeof(WolverineCommandCompletedHandler));
        return options;
    }

    /// <summary>
    /// On a worker's Wolverine options: responds to each message a Scry server sent as a command with
    /// how it ended, once its handler has returned. For a message that ends in the error queue, add
    /// <see cref="AndScryFailure"/> to the error policy that sends it there.
    /// </summary>
    public static WolverineOptions UseScryCommands(this WolverineOptions options)
    {
        options.Policies.AddMiddleware(typeof(ScryCommandMiddleware), _ => _.MessageType != typeof(WolverineCommandCompleted));
        return options;
    }

    /// <summary>
    /// On a worker's error policy, after <c>MoveToErrorQueue</c>: also answers the command whose
    /// message it is as failed, to the server waiting for it — which would otherwise hold it pending
    /// until it gave up on it.
    /// </summary>
    public static IAdditionalActions AndScryFailure(this IAdditionalActions actions) =>
        actions.And(ScryCommandMiddleware.Failed, "Answers the Scry command whose message this is as failed.");

    /// <summary>
    /// What a handler of a command with a result answers with, sent back to the server in the
    /// response. Serialized as the server's own results are, so it arrives as the client expects it.
    /// </summary>
    public static void SetScryResult(this IMessageContext context, object result)
    {
        if (context.Envelope is not { } envelope ||
            !envelope.Headers.ContainsKey(ScryCommandHeaders.CommandId))
        {
            throw new InvalidOperationException("SetScryResult was called while handling a message no Scry server sent as a command.");
        }

        ScryCommandMiddleware.SetResult(envelope, JsonSerializer.Serialize(result, result.GetType(), ScryJson.Options));
    }
    // end-snippet
}
