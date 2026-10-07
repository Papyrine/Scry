/// <summary>
/// The disclosure audit as one processor runs it: the settings, the sink once it has been found, who
/// an answer is recorded under, and how an address is made. Null on a processor whose host never
/// turned the audit on, which is the one check everything else here hides behind.
/// </summary>
sealed class DisclosureRuntime(ScryDisclosureOptions settings, ScryOptions options, string stamp)
{
    Lock gate = new();
    IScryDisclosureSink? sink;

    public ScryDisclosureOptions Settings => settings;

    /// <summary>The schema stamp every event is recorded with.</summary>
    public string Stamp => stamp;

    public DateTimeOffset Now => settings.Clock.GetUtcNow();

    /// <summary>
    /// Finds the sink, where it has not been found already. Called at startup with the host's root
    /// services, and again on first use for a host that never ran the startup checks — so a
    /// processor made by hand records as one mapped by <c>MapScry</c> does.
    /// </summary>
    public void Attach(IServiceProvider services) =>
        Sink(services);

    // Taken from the host's services where AddScry put it there, which is what makes the container
    // the one that disposes it and lets anything else that records — a reviewer's questions — reach
    // the same one. A processor made by hand has no such registration and makes its own, once.
    IScryDisclosureSink Sink(IServiceProvider services)
    {
        if (sink is { } attached)
        {
            return attached;
        }

        var registered = services.GetService<IScryDisclosureSink>();
        lock (gate)
        {
            return sink ??= registered ?? settings.Build(services);
        }
    }

    /// <summary>
    /// Who an answer is recorded under: what the transport said, or failing that what the host's
    /// resolver or the current request says. Refused where nobody can be named and the host has not
    /// said that is acceptable.
    /// </summary>
    public string? Caller(string? stated, IServiceProvider services)
    {
        var caller = stated ?? Resolve(services);
        Require(caller);
        return caller;
    }

    /// <summary>Refuses an answer that is to be recorded and has nobody to be recorded under.</summary>
    public void Require(string? caller)
    {
        if (caller is null &&
            !settings.AllowAnonymous)
        {
            throw new ScryDisclosureException($"The disclosure audit is on and this call has no caller to be recorded under, so it was not answered. ScryOptions.Caller names the caller over HTTP, the hub and MCP; ScryDisclosureOptions.{nameof(ScryDisclosureOptions.Caller)} names it for any other transport. Set {nameof(ScryDisclosureOptions.AllowAnonymous)} where an answer may be recorded against nobody.");
        }
    }

    string? Resolve(IServiceProvider services)
    {
        if (settings.Caller is { } resolver)
        {
            return resolver(services);
        }

        if (services.GetService<IHttpContextAccessor>()?.HttpContext is { } context)
        {
            return options.Caller(context);
        }

        return null;
    }

    /// <summary>Starts recording one answer.</summary>
    /// <param name="request">What was asked — a query, an attachment fetch, a command — or null where nothing was.</param>
    /// <param name="source">The source the answer is about, or empty where it is about none.</param>
    /// <param name="caller">Who the transport says is asking.</param>
    /// <param name="services">The call's services.</param>
    public DisclosureCapture Begin(object? request, string source, string? caller, IServiceProvider services) =>
        new(this, services, request, source, Caller(caller, services));

    /// <summary>
    /// Starts recording an answer that may turn out to need no record: a query whose root the host
    /// left out, which is recorded only if it reads something that was not. Nobody is required to be
    /// named until that is known.
    /// </summary>
    public DisclosureCapture BeginUndecided(object? request, string source, string? caller, IServiceProvider services, IHeaderDictionary responseHeaders) =>
        new(this, services, request, source, caller ?? Resolve(services))
        {
            Undecided = responseHeaders
        };

    public void Append(ScryDisclosureBatch batch, IServiceProvider services)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            Sink(services).Append(batch);
        }
        catch (Exception exception)
        {
            throw Refused(exception, started);
        }

        QueryRecorder.DisclosureAccepted(Stopwatch.GetElapsedTime(started), failure: null);
    }

    public async ValueTask AppendAsync(ScryDisclosureBatch batch, IServiceProvider services, Cancel cancel)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await Sink(services).AppendAsync(batch, cancel);
        }
        // The caller going away while the sink was asked is the one failure that is nobody's: there
        // is nobody left to send to, and nothing was sent.
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        // Every other failure, a sink's own cancellation among them, means the record was not
        // accepted — which is a failure of the answer, not an abandoned request.
        catch (Exception exception)
        {
            throw Refused(exception, started);
        }

        QueryRecorder.DisclosureAccepted(Stopwatch.GetElapsedTime(started), failure: null);
    }

    static ScryDisclosureException Refused(Exception exception, long started)
    {
        QueryRecorder.DisclosureAccepted(Stopwatch.GetElapsedTime(started), exception);
        if (exception is ScryDisclosureException refused)
        {
            return refused;
        }

        return new($"The disclosure audit did not accept the record of this answer, so it was not sent: {exception.Message}", exception);
    }

    /// <summary>
    /// The address of content already laid out as its kind byte followed by its bytes, which is how a
    /// row is written into the capture's scratch buffer — so a row is hashed where it lies.
    /// </summary>
    public ScryDisclosureAddress Address(ReadOnlySpan<byte> tagged)
    {
        Span<byte> hash = stackalloc byte[ScryDisclosureAddress.Size];
        if (settings.AddressKey is { } key)
        {
            HMACSHA256.HashData(key, tagged, hash);
        }
        else
        {
            SHA256.HashData(tagged, hash);
        }

        return ScryDisclosureAddress.From(hash);
    }

    /// <summary>
    /// A hash to feed in pieces, for content that is not lying behind its kind byte: a binary value,
    /// a request. Keyed as <see cref="Address(ReadOnlySpan{byte})"/> is, so both make the same
    /// address of the same content.
    /// </summary>
    public IncrementalHash Incremental()
    {
        if (settings.AddressKey is { } key)
        {
            return IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        }

        return IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }
}
