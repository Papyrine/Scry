# Scry.Server.Wolverine

Carries [Scry](https://github.com/Papyrine/Scry) [commands](https://github.com/Papyrine/Scry/blob/main/docs/commands.md) over [Wolverine](https://wolverinefx.net/): a command a client sends is sent to the handler that handles it, and its outcome comes back to the client when the handler is done.

On the Scry server:

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxPendingCommands = 100;
        _.UseWolverineCommands(_ => _.For<RepriceOrder>());
    });
builder.Host.UseWolverine(
    _ =>
    {
        _.AddScryCommandCompletions();
        _.PublishMessage<RepriceOrder>().ToRabbitQueue("worker");
    });
```

On the worker:

```cs
builder.Host.UseWolverine(
    _ =>
    {
        _.UseScryCommands();
        _.Policies.OnAnyException()
            .RetryTimes(3)
            .Then.MoveToErrorQueue()
            .AndScryFailure();
    });
```

The server validates, authorizes and binds the command as it would for an in-process handler, then sends it with the command's id and caller as headers, routed by the application's own routing. The handler is an ordinary Wolverine handler. Once it has returned, middleware responds to the node that sent the command. A message that ends in the error queue is answered as failed by `AndScryFailure`, added to the error policy that sends it there. A handler of a command with a result answers with `context.SetScryResult(result)`: its return value cascades as messages, so the result travels apart from it.

It claims nothing until told: a Wolverine message is any class. Everything it does not claim stays with its in-process handler.

This package carries no change backplane. A handler's writes reach live queries through the notification the server raises on completion for a targeted command, a change probe, or the poll.

Docs: [Commands](https://github.com/Papyrine/Scry/blob/main/docs/commands.md)
