using System.IO.Pipelines;

/// <summary>
/// A scripted command server, for tests of what a client makes of the answers: each command request —
/// a command sent, or one asked for again by its id — is answered by the next step of the script, and
/// what each one asked is recorded. The capabilities read is answered apart, by <see cref="Capabilities"/>.
/// </summary>
/// <remarks>
/// Steps are functions of the command's id, which the client mints and the stub only learns from the
/// request. A request past the end of the script is refused for good, so a test that asks more often
/// than it meant to fails rather than spins. Public API only, since the component tests link it.
/// </remarks>
sealed class CommandStub
{
    Queue<Func<Guid, HttpResponseMessage>> steps = new();

    public CommandStub(params Func<Guid, HttpResponseMessage>[] steps)
    {
        foreach (var step in steps)
        {
            this.steps.Enqueue(step);
        }
    }

    /// <summary>Answers the capabilities read. No route unless set, which is a server with commands off.</summary>
    public Func<HttpResponseMessage> Capabilities { get; set; } = () => new(HttpStatusCode.NotFound);

    public int CapabilityReads { get; private set; }

    /// <summary>Each command request, as method and path.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Each command sent.</summary>
    public List<CommandRequest> Sent { get; } = [];

    public Guid LastId { get; private set; }

    public HttpMessageHandler Handler() =>
        new StubHandler(this);

    /// <summary>A client over the stub that asks again at once, since what is pinned is whether it does.</summary>
    public ScryClient Client(TimeSpan? commandWait = null)
    {
        var http = new HttpClient(Handler())
        {
            BaseAddress = new("http://localhost")
        };
        var client = ScryClient.ForHttp(http, "/api/query");
        client.Reconnect = new Immediate();
        if (commandWait is { } wait)
        {
            client.CommandWait = wait;
        }

        return client;
    }

    async Task<HttpResponseMessage> Respond(HttpRequestMessage request, Cancel cancel)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith($"/{ScryCommandProtocol.CapabilitiesRoute}", StringComparison.Ordinal))
        {
            lock (steps)
            {
                CapabilityReads++;
            }

            return Capabilities();
        }

        Guid id;
        if (request.Method == HttpMethod.Post)
        {
            var sent = ScryJson.DeserializeCommandRequest(await request.Content!.ReadAsByteArrayAsync(cancel));
            id = sent.Id;
            lock (steps)
            {
                Sent.Add(sent);
            }
        }
        else
        {
            id = Guid.Parse(path[(path.LastIndexOf('/') + 1)..]);
        }

        lock (steps)
        {
            LastId = id;
            Requests.Add($"{request.Method} {path}");
            if (steps.TryDequeue(out var step))
            {
                return step(id);
            }
        }

        return Refusal(HttpStatusCode.BadRequest, ScryErrorCode.Validation, "Past the end of the script.")(id);
    }

    /// <summary>A command decided within the sync window: one receipt, as JSON.</summary>
    public static Func<Guid, HttpResponseMessage> Json(CommandStatus status, object? result = null, string? error = null) =>
        id =>
        {
            var content = new ByteArrayContent(ScryJson.SerializeToUtf8(Receipt(id, status, result, error)));
            content.Headers.ContentType = new("application/json");
            return new(HttpStatusCode.OK)
            {
                Content = content
            };
        };

    /// <summary>A stream of receipts that has all arrived, and then ends — cut, unless an event says otherwise.</summary>
    public static Func<Guid, HttpResponseMessage> Events(params Func<Guid, string>[] events) =>
        id => new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(events.Select(_ => _(id))), Encoding.UTF8, ScryLive.ContentType)
        };

    /// <summary>The <c>result</c> event carrying a receipt.</summary>
    public static Func<Guid, string> Result(CommandStatus status, object? result = null, string? error = null) =>
        id => Event(ScryLive.Result, ScryJson.Serialize(Receipt(id, status, result, error)));

    /// <summary>The <c>end</c> event a server sends to bound a stream's life.</summary>
    public static Func<Guid, string> End() =>
        _ => Event(ScryLive.End, Encoding.UTF8.GetString(ScryJson.SerializeToUtf8(new ScryLiveEnd(Reconnect: true) {Reason = "lifetime"})));

    public static Func<Guid, string> Ping() =>
        _ => Event(ScryLive.Ping, "");

    /// <summary>A status with the endpoint's own body, or — with no code — something in the way's.</summary>
    public static Func<Guid, HttpResponseMessage> Refusal(HttpStatusCode status, ScryErrorCode? code, string message = "refused") =>
        _ =>
        {
            if (code is not { } known)
            {
                return new(status)
                {
                    Content = new StringContent("<html>not here</html>", Encoding.UTF8, "text/html")
                };
            }

            return new(status)
            {
                Content = new StringContent(
                    ScryJson.Serialize(
                        new ScryError(message)
                        {
                            Code = known
                        }),
                    Encoding.UTF8,
                    "application/json")
            };
        };

    public static HttpResponseMessage CapabilitiesOf(params string[] commands)
    {
        var content = new ByteArrayContent(ScryJson.SerializeToUtf8(CommandCapabilities.Create(commands)));
        content.Headers.ContentType = new("application/json");
        return new(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    public static CommandReceipt Receipt(Guid id, CommandStatus status, object? result = null, string? error = null) =>
        CommandReceipt.Create(id, status) with
        {
            Result = result is null ? null : JsonSerializer.SerializeToElement(result, ScryJson.Options),
            Error = error
        };

    static string Event(string name, string data) =>
        $"event: {name}\ndata: {data}\n\n";

    sealed class StubHandler(CommandStub stub) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel) =>
            stub.Respond(request, cancel);
    }

    sealed class Immediate :
        IScryRetryPolicy
    {
        public TimeSpan? NextDelay(ScryRetryContext context) =>
            TimeSpan.Zero;
    }
}

/// <summary>A stream of receipts written to while it is being read, as a pending command's is.</summary>
sealed class HeldReceipts :
    IAsyncDisposable
{
    Pipe pipe = new();
    Guid id;

    public Func<Guid, HttpResponseMessage> Step() =>
        id =>
        {
            this.id = id;
            var content = new StreamContent(pipe.Reader.AsStream());
            content.Headers.ContentType = new(ScryLive.ContentType);
            return new(HttpStatusCode.OK)
            {
                Content = content
            };
        };

    public async Task Send(Func<Guid, string> item)
    {
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(item(id)));
        await pipe.Writer.FlushAsync();
    }

    /// <summary>Ends the stream where it stands, as a connection that was cut does.</summary>
    public ValueTask DisposeAsync() =>
        pipe.Writer.CompleteAsync();
}

[ScryCommand("RenameThing", Target = "Thing", Keys = ["Id"])]
public sealed class RenameThing
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

[ScryCommand("CreateThing", Result = typeof(ThingCreated))]
public sealed class CreateThing
{
    public string Name { get; init; } = "";
}

public sealed class ThingCreated
{
    public int Id { get; init; }
}
