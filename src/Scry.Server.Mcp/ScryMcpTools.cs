using System.ComponentModel;

// The tools an agent is served. Each is a thin transport over ScryProcessor, as ScryHub's methods are:
// everything a query or command is subject to — validation, the allow-list, policies, limits, audit — is
// the processor's, so it applies here unchanged. Requests are read by ScryJson and nothing else, so the
// wire's strictness holds for an agent exactly as for a generated client.
static class ScryMcpTools
{
    public static IEnumerable<McpServerTool> For(ScryMcpAccess access)
    {
        if (access == ScryMcpAccess.Off)
        {
            yield break;
        }

        yield return Create(nameof(DescribeSchema), "describe_schema", ScryMcpGrammar.DescribeSchema, readOnly: true);
        yield return Create(nameof(Query), "query", ScryMcpGrammar.Query, readOnly: true);
        if (access != ScryMcpAccess.ReadWrite)
        {
            yield break;
        }

        yield return Create(nameof(SendCommand), "send_command", ScryMcpGrammar.SendCommand, readOnly: false);
        yield return Create(nameof(CommandReceipt), "command_receipt", ScryMcpGrammar.CommandReceipt, readOnly: true);
        yield return Create(nameof(Capabilities), "capabilities", ScryMcpGrammar.Capabilities, readOnly: true);
    }

    static McpServerTool Create(string method, string name, string description, bool readOnly) =>
        McpServerTool.Create(
            typeof(ScryMcpTools).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!,
            target: null,
            new()
            {
                Name = name,
                Description = description,
                ReadOnly = readOnly,
                Destructive = !readOnly,
                Idempotent = readOnly,
                OpenWorld = false
            });

    static CallToolResult DescribeSchema(IServiceProvider services)
    {
        var request = Request.From(services);
        var introspection = request.Processor.Describe();
        // An agent that may not write is not told what writing would look like.
        if (request.Options.Mcp != ScryMcpAccess.ReadWrite)
        {
            introspection = introspection with
            {
                Commands = []
            };
        }

        return Answer(ScryJson.Serialize(introspection));
    }

    static async Task<CallToolResult> Query(
        IServiceProvider services,
        [Description("The source the query starts from, as describe_schema names it.")]
        string root,
        [Description("The operators to apply, in order: an array of objects each with a $type.")]
        JsonElement pipeline,
        Cancel cancel)
    {
        var request = Request.From(services);
        QueryRequest parsed;
        try
        {
            parsed = ScryJson.DeserializeRequest(QueryJson(root, pipeline));
        }
        catch (ScryWireException exception)
        {
            return Refuse(exception.Message, ScryErrorCode.WireFormat);
        }

        try
        {
            using var output = new PooledBufferWriter();
            var limited = ResponseBudget.For(request.Options)?.Charging(output) ?? output;
            var fallback = await request.Processor.TryExecuteBufferedAsync(
                parsed,
                request.Data,
                request.Services,
                request.Headers,
                new HeaderDictionary(),
                limited,
                cancel: cancel);

            // Written through the same budget, so the envelope a drifted client is answered with is
            // bounded as any other.
            if (fallback is not null)
            {
                ResponseWriter.Write(limited, fallback);
            }

            return Answer(Encoding.UTF8.GetString(output.WrittenMemory.Span));
        }
        catch (Exception exception) when (!cancel.IsCancellationRequested)
        {
            return QueryFailure(exception);
        }
    }

    static async Task<CallToolResult> SendCommand(
        IServiceProvider services,
        [Description("The command's name, as describe_schema lists it under commands.")]
        string command,
        [Description("The command's properties, camel-cased, as an object.")]
        JsonElement payload,
        Cancel cancel)
    {
        var request = Request.From(services);
        var json = ScryJson.Serialize(CommandRequest.Create(command, Guid.NewGuid(), payload));
        if (Encoding.UTF8.GetByteCount(json) > request.Options.MaxCommandBytes)
        {
            return Refuse($"A command may be at most {request.Options.MaxCommandBytes} bytes.", ScryErrorCode.PayloadTooLarge);
        }

        CommandRequest parsed;
        try
        {
            parsed = ScryJson.DeserializeCommandRequest(json);
        }
        catch (ScryWireException exception)
        {
            return Refuse(exception.Message, ScryErrorCode.WireFormat);
        }

        return await FirstReceipt(
            request.Processor.SendCommand(parsed, request.Data, request.Services, request.Headers, request.Caller, cancel),
            cancel);
    }

    static async Task<CallToolResult> CommandReceipt(
        IServiceProvider services,
        [Description("The id the command's first receipt carried.")]
        string id,
        Cancel cancel)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return Refuse("A command is asked for by the id its first receipt carried.", ScryErrorCode.WireFormat);
        }

        var request = Request.From(services);
        return await FirstReceipt(request.Processor.Receipt(parsed, request.Caller, cancel), cancel);
    }

    static CallToolResult Capabilities(IServiceProvider services)
    {
        var request = Request.From(services);
        return Answer(ScryJson.Serialize(request.Processor.Capabilities(request.Data, request.Services, request.Headers)));
    }

    // The receipt as it stands: final where the command finished within the sync window, otherwise the
    // pending one. Leaving the rest unread stops the waiting and nothing else — an accepted command runs to
    // its end, and is asked for again by its id.
    static async Task<CallToolResult> FirstReceipt(IAsyncEnumerable<CommandReceipt> receipts, Cancel cancel)
    {
        try
        {
            await using var enumerator = receipts.GetAsyncEnumerator(cancel);
            if (!await enumerator.MoveNextAsync())
            {
                return Refuse("Command dispatch failed.", ScryErrorCode.ExecutionFailed);
            }

            return Answer(ScryJson.Serialize(enumerator.Current));
        }
        catch (Exception exception) when (!cancel.IsCancellationRequested)
        {
            return CommandFailure(exception);
        }
    }

    // The request as the wire spells it. The agent supplies only what it chose; the version is this
    // server's, since an agent writes against the vocabulary it was just told, not a generated model.
    static string QueryJson(string root, JsonElement pipeline)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", WireFormat.Version);
            writer.WriteString("root", root);
            writer.WritePropertyName("pipeline");
            pipeline.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    // The same answers the endpoints give, coded the same way: what the agent did is repeated to it, so it
    // can correct itself, and anything else is the fixed text and nothing more.
    static CallToolResult QueryFailure(Exception exception) =>
        exception switch
        {
            ScryWireException wire => Refuse(wire.Message, ScryErrorCode.WireFormat),
            ScryValidationException validation => Refuse(validation.Message, ErrorCodes.Classify(validation)),
            ScryPermissionException permission => Refuse(permission.Message, ScryErrorCode.Forbidden),
            _ => Refuse("Query execution failed.", ScryErrorCode.ExecutionFailed)
        };

    static CallToolResult CommandFailure(Exception exception) =>
        exception switch
        {
            ScryWireException wire => Refuse(wire.Message, ScryErrorCode.WireFormat),
            ScryValidationException validation => Refuse(validation.Message, ErrorCodes.Classify(validation)),
            ScryPermissionException permission => Refuse(permission.Message, ScryErrorCode.Forbidden),
            ScryCommandNotFoundException notFound => Refuse(notFound.Message, ScryErrorCode.NotFound),
            ScryCommandLimitException limit => Refuse(limit.Message, ScryErrorCode.CommandLimit),
            _ => Refuse("Command dispatch failed.", ScryErrorCode.ExecutionFailed)
        };

    static CallToolResult Answer(string json) =>
        new()
        {
            Content = [new TextContentBlock { Text = json }]
        };

    static CallToolResult Refuse(string message, ScryErrorCode code) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = ScryJson.Serialize(
                        new ScryError(message)
                        {
                            Code = code
                        })
                }
            ]
        };

    // What one tool call runs against: the HTTP request it arrived on, as MapScry's endpoints run against
    // theirs. Its scope is the one row policies read the caller from, its headers are the ones they are
    // given, and its user is who a command is sent as.
    sealed class Request(HttpContext http)
    {
        public static Request From(IServiceProvider services)
        {
            var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext ??
                       throw new InvalidOperationException("Scry's MCP tools are served over HTTP, by MapScryMcp.");
            return new(http);
        }

        public IServiceProvider Services => http.RequestServices;

        public ScryProcessor Processor => Services.GetRequiredService<ScryProcessor>();

        public ScryOptions Options => Services.GetRequiredService<ScryOptions>();

        public DbContext Data => (DbContext)Services.GetRequiredService(Options.ContextType);

        public IHeaderDictionary Headers => http.Request.Headers;

        public string? Caller => Options.Caller(http);
    }
}
