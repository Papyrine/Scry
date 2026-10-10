/// <summary>
/// What the disclosure audit's tests share: a processor with the audit on and the store it records
/// to, the two ways of asking it a question, and the record laid out for a snapshot.
/// </summary>
static class Disclosures
{
    public const string Caller = "alice";

    /// <summary>
    /// A processor over the shared model with the audit on, recording to a store of its own. Every
    /// answer is recorded under <see cref="Caller"/> unless <paramref name="configure"/> says otherwise.
    /// </summary>
    public static (ScryProcessor Processor, ScryMemoryDisclosureStore Store) Audited(
        Action<ScryOptions>? extra = null,
        Action<ScryDisclosureOptions>? configure = null)
    {
        var store = new ScryMemoryDisclosureStore();
        return (Audited(store, extra, configure), store);
    }

    /// <summary>The same, recording through <paramref name="sink"/>.</summary>
    public static ScryProcessor Audited(
        IScryDisclosureSink sink,
        Action<ScryOptions>? extra = null,
        Action<ScryDisclosureOptions>? configure = null) =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.UseDisclosureAudit(
                sink,
                disclosure =>
                {
                    disclosure.Caller = _ => Caller;
                    disclosure.Node = "node-1";

                    // One second on with every reading, so events sort the way they were made
                    // whatever the machine's clock resolves to.
                    disclosure.Clock = new SteppingClock();
                    configure?.Invoke(disclosure);
                });
            extra?.Invoke(options);
        });

    /// <summary>
    /// Asks the way the endpoint does: the answer written into a buffer as response bytes. Returns
    /// what a transport would then send.
    /// </summary>
    public static async Task<byte[]> Buffered(
        ScryProcessor processor,
        QueryRequest request,
        IServiceProvider? services = null,
        BinaryPartCollector? binary = null,
        IHeaderDictionary? responseHeaders = null,
        string? caller = null)
    {
        await using var context = TestContext.CreateSeeded();
        var output = new ArrayBufferWriter<byte>();
        var fallback = await processor.TryExecuteBufferedAsync(
            request,
            context,
            services ?? EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            responseHeaders ?? new HeaderDictionary(),
            output,
            binary: binary,
            caller: caller);
        if (fallback is not null)
        {
            ResponseWriter.Write(output, fallback);
        }

        return output.WrittenSpan.ToArray();
    }

    /// <summary>Asks the way a host calling the processor directly does: the answer as an object.</summary>
    public static QueryResponse Direct(ScryProcessor processor, QueryRequest request, IServiceProvider? services = null)
    {
        using var context = TestContext.CreateSeeded();
        return processor.Execute(request, context, services ?? EmptyServiceProvider.Instance);
    }

    /// <summary>
    /// Everything recorded for a caller, oldest first, laid out to be read: each event with its
    /// request, how it ended, and its units with their content as text.
    /// </summary>
    public static async Task<List<object>> Recorded(ScryMemoryDisclosureStore store, string? caller = Caller)
    {
        var entries = await store
            .ReceivedBy(caller, DateTimeOffset.MinValue, DateTimeOffset.MaxValue)
            .ToListAsync();
        entries.Reverse();

        var recorded = new List<object>();
        foreach (var entry in entries)
        {
            recorded.Add(Laid((await store.Reconstruct(entry.Event.Id))!));
        }

        return recorded;
    }

    public static object Laid(ScryDisclosedResponse answer) =>
        new
        {
            // As text, so the first of each enum is written rather than left out as a default.
            Kind = answer.Event.Kind.ToString(),
            answer.Event.Source,
            answer.Event.Caller,
            answer.Event.Node,
            answer.Event.Sensitive,
            Request = Text(answer.Request),
            Outcome = answer.Close?.Outcome.ToString(),
            Released = answer.Close?.Units.ToString(),
            Response = answer.Close?.Response?.ToString(),
            Fields = answer.Shape?.Fields.Select(Field),
            Units = answer.Units.Select(_ => new
            {
                _.Ordinal,
                Address = _.Content.Address.ToString(),
                Kind = _.Content.Kind.ToString(),
                Content = Text(_.Content.Bytes),
                _.Erased,
                Rows = _.Entities.Select(Row)
            })
        };

    /// <summary>A member a query read, on one line: whose it is, what was done with it.</summary>
    public static string Field(ScryDisclosureField field)
    {
        var text = $"{field.Source}.{field.Member}: {field.Use}";
        if (field.Sensitive)
        {
            return text + ", sensitive";
        }

        return text;
    }

    /// <summary>A row a unit was read from, on one line: its source and key, and how it was reached.</summary>
    public static string Row(ScryDisclosureEntity entity)
    {
        var text = $"{entity.Source}{entity.Key}";
        if (entity.Via.Length == 0)
        {
            return text;
        }

        return $"{text} via {entity.Via}";
    }

    /// <summary>What the last answer recorded for a caller read, and which rows each of its units came from.</summary>
    public static async Task<object> Shape(ScryMemoryDisclosureStore store, string? caller = Caller)
    {
        var last = (await Events(store, caller))[^1];
        var answer = (await store.Reconstruct(last.Event.Id))!;
        return new
        {
            answer.Event.Sensitive,
            Fields = answer.Shape?.Fields.Select(Field),
            Units = answer.Units.Select(_ => new
            {
                Content = Text(_.Content.Bytes),
                Rows = _.Entities.Select(Row)
            })
        };
    }

    // Only ever asked to translate: what these tests hand the processor is the request LINQ becomes.
    static ScryClient translator = new((_, _) => throw new InvalidOperationException("This client only translates."));

    /// <summary>A source to write LINQ against, for the request it translates to.</summary>
    public static IQueryable<T> From<T>(string source) =>
        translator.Source<T>(source);

    /// <summary>The addresses of an event's units, in order: what two answers are compared by.</summary>
    public static async Task<List<string>> Addresses(ScryMemoryDisclosureStore store, Guid eventId) =>
        [.. (await store.Reconstruct(eventId))!.Units.Select(_ => _.Content.Address.ToString())];

    public static async Task<List<ScryDisclosureEntry>> Events(ScryMemoryDisclosureStore store, string? caller = Caller)
    {
        var entries = await store
            .ReceivedBy(caller, DateTimeOffset.MinValue, DateTimeOffset.MaxValue)
            .ToListAsync();
        entries.Reverse();
        return entries;
    }

    static string? Text(ReadOnlyMemory<byte>? bytes)
    {
        if (bytes is not {Length: > 0} held)
        {
            return null;
        }

        return Encoding.UTF8.GetString(held.Span);
    }

    /// <summary>The active employees' names, in order: three rows of one member.</summary>
    public static QueryRequest Names() =>
        QueryRequest.Create(
            "Employee",
            [
                new WhereOp(new MemberNode(["Active"])),
                new OrderByOp(new MemberNode(["Name"]), Descending: false),
                new SelectOp(new([new("Name", new NodeValue(new MemberNode(["Name"])))]))
            ]);
}

/// <summary>A clock that moves on a second every time it is read.</summary>
sealed class SteppingClock :
    TimeProvider
{
    Lock gate = new();
    DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            now = now.AddSeconds(1);
            return now;
        }
    }
}

/// <summary>
/// A sink that keeps a copy of every batch it is handed, in order, and passes each on. The copy is
/// made by serializing the batch and reading it back, so it is also what a sink that stores the bytes
/// would hold.
/// </summary>
sealed class TappedSink(IScryDisclosureSink? inner = null) :
    IScryDisclosureSink
{
    public List<ScryDisclosureBatch> Batches { get; } = [];

    /// <summary>Called with each batch before it is passed on, where a test wants to look around.</summary>
    public Action<ScryDisclosureBatch>? Accepting { get; set; }

    public void Append(ScryDisclosureBatch batch)
    {
        Accepting?.Invoke(batch);
        var bytes = new ArrayBufferWriter<byte>();
        batch.Serialize(bytes);
        var copy = ScryDisclosureBatch.Deserialize(bytes.WrittenSpan);
        inner?.Append(batch);
        lock (Batches)
        {
            Batches.Add(copy);
        }
    }

    public ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        Append(batch);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A sink that accepts a number of batches and then refuses every one after.</summary>
sealed class RefusingSink(int accepting = 0, Func<Exception>? failure = null, IScryDisclosureSink? inner = null) :
    IScryDisclosureSink
{
    int accepted;

    public int Asked { get; private set; }

    public void Append(ScryDisclosureBatch batch)
    {
        Asked++;
        if (accepted >= accepting)
        {
            throw failure?.Invoke() ?? new InvalidOperationException("The store is down.");
        }

        accepted++;
        inner?.Append(batch);
    }

    public ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        Append(batch);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A store that can be taken down and brought back. Down, it refuses every batch; up, it passes each
/// on. It answers for the store behind it either way, as a store that is unreachable still exists.
/// </summary>
sealed class SwitchedSink(IScryDisclosureSink inner) :
    IScryDisclosureSink,
    IScryDisclosureStatus
{
    bool down;

    public bool Down
    {
        get => Volatile.Read(ref down);
        set => Volatile.Write(ref down, value);
    }

    public void Append(ScryDisclosureBatch batch)
    {
        if (Down)
        {
            throw new InvalidOperationException("The store is down.");
        }

        inner.Append(batch);
    }

    public ValueTask AppendAsync(ScryDisclosureBatch batch, Cancel cancel)
    {
        Append(batch);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ScryDisclosureStoreStatus> Status(Cancel cancel = default)
    {
        if (inner is IScryDisclosureStatus status)
        {
            return status.Status(cancel);
        }

        return new(new ScryDisclosureStoreStatus(Pending: 0, PendingBytes: 0, OldestPending: null, Events: 0));
    }
}

sealed class DisclosureAddressConverter :
    WriteOnlyJsonConverter<ScryDisclosureAddress>
{
    public override void Write(VerifyJsonWriter writer, ScryDisclosureAddress value) =>
        writer.WriteValue(value.ToString());
}
