namespace Scry;

/// <summary>
/// Executes a query request against a <see cref="DbContext"/>, applying validation, allow-list,
/// policies, and shaping. This is the programmatic entry point used by the HTTP endpoint and is also
/// usable directly (other transports, tests).
/// </summary>
public sealed partial class ScryProcessor
{
    QueryExecutor executor;
    Schema schema;
    ScryOptions options;
    SubscriptionHub subscriptions;

    internal ScryProcessor(Schema schema, ScryOptions options)
    {
        this.schema = schema;
        this.options = options;
        executor = new(schema, options);
        Changes = new();
        PolicyCache = new(schema.CachedPolicies, Changes);
        subscriptions = new(options, Changes);
        if (options.Disclosure is { } audit)
        {
            disclosure = new(audit, options, schema.Stamp);
        }

        InitializeCommands();
    }

    /// <summary>
    /// The name of a source whose rows depend on who asked — one carrying a row or attachment policy —
    /// or null where no source does. Read at startup to refuse a caching setup that would hand one
    /// caller's rows to the next.
    /// </summary>
    internal IEnumerable<CallerDependence> CallerDependentSources => schema.CallerDependentSources;

    /// <summary>Describes the allow-listed query surface for tooling (the query explorer).</summary>
    public ScryIntrospection Describe() => schema.Describe(options);

    /// <summary>
    /// A hash of this server's allow-listed surface. Advertised on every response so a client can
    /// compare it against the stamp it was generated with and detect a drifted model.
    /// </summary>
    public string SchemaStamp => schema.Stamp;

    /// <summary>
    /// The cached row policies' answers: what a host invalidates when a grant changed, and primes when
    /// it has just written rows somebody is about to read. Also registered as a singleton by
    /// <c>AddScry</c>, which is how a host reaches it without holding the processor.
    /// </summary>
    public ScryPolicyCache PolicyCache { get; }

    /// <summary>
    /// Where a host reports that data changed, so the live queries reading it are asked again. Also
    /// registered as a singleton by <c>AddScry</c>, which is how a host reaches it without holding the
    /// processor.
    /// </summary>
    public ScryChanges Changes { get; }

    /// <summary>
    /// Confirms the model's annotations match its live EF mapping (e.g. a <c>[Queryable]</c> type is
    /// really an entity, a <c>[QueryableComplex]</c> type is really a complex type), throwing a
    /// directed error otherwise. Called once at startup by <c>MapScry</c>; safe to call from other
    /// hosts that have a <see cref="DbContext"/>.
    /// </summary>
    public void ValidateAgainstModel(DbContext data)
    {
        schema.ValidateAgainstModel(data.Model, options.ContextType);
        EnsureRowsCanBeNamed(data.Model);
    }

    /// <summary>
    /// Confirms, where the disclosure audit is on, that every source has something a row of it can be
    /// recorded by — or that the host has said it knows there is nothing.
    /// </summary>
    /// <remarks>
    /// Asked of the model rather than of the annotations: a type mapped with <c>HasNoKey</c> is still a
    /// <c>[Queryable]</c> to the schema, and a key EF holds in shadow is a key nothing here can read.
    /// A source with no row identity is recorded as content alone, so who received a given row of it
    /// can never be asked. That is a decision for a host to make, and is not made by default.
    /// </remarks>
    void EnsureRowsCanBeNamed(IModel model)
    {
        if (options.Disclosure is not { } audit)
        {
            return;
        }

        foreach (var source in schema.Sources.OrderBy(_ => _.Name, StringComparer.Ordinal))
        {
            var type = source.ClrType;

            // Opted in and not mapped, which is allowed only where the host waived it: nothing is
            // ever read from it, so there is nothing to record.
            if (source.Kind != SourceKind.Poco &&
                model.FindEntityType(type) is null)
            {
                continue;
            }

            if (audit.Excludes(type) ||
                Acknowledged(audit, type) ||
                DisclosurePlanner.Key(model, audit, type) is not null)
            {
                continue;
            }

            throw new($"The disclosure audit is on and '{source.Name}' ({type.Name}) has nothing a row of it can be recorded by: it is supplied from memory, or mapped with no key, or keyed by a value EF holds with no property to read it through. Say what identifies a row — _.Key<{type.Name}>(_ => _.Code) in UseDisclosureAudit's settings — or acknowledge that nothing does with _.Unkeyed<{type.Name}>(), which records what is sent from it as content with no row to ask about.");
        }
    }

    // Said of a type or of one it derives from: a hierarchy with nothing to identify a row by has
    // nothing to identify one of its subtypes' rows by either.
    static bool Acknowledged(ScryDisclosureOptions audit, Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (audit.Acknowledged.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Confirms every entity and view source is a type the context maps, throwing a directed error
    /// otherwise. The schema allows an opted-in type the context does not map — a model assembly may
    /// serve more than one context — but a host that serves such a source serves a 500 on every query
    /// of it, since introspection advertises what <c>Set&lt;T&gt;()</c> then refuses. Called once by
    /// <c>MapScry</c>; safe for any host. Waived by <see cref="ScryOptions.AllowUnmappedSources"/>,
    /// where a query naming such a source is rejected as unknown instead.
    /// </summary>
    public void EnsureSourcesMapped(DbContext data)
    {
        if (options.AllowUnmappedSources)
        {
            return;
        }

        foreach (var source in schema.Sources)
        {
            if (source.Kind == SourceKind.Poco ||
                data.Model.FindEntityType(source.ClrType) is not null)
            {
                continue;
            }

            throw new(
                $"Source '{source.Name}' ({source.ClrType.Name}) is opted in but {options.ContextType.Name} does not map it, so every query of it would fail. Add a DbSet<{source.ClrType.Name}> or map it in OnModelCreating, make it a [QueryablePoco] supplied by AddPocoSource, or remove the opt-in.");
        }
    }

    /// <summary>
    /// Confirms every policy the schema will apply — row, attachment, and cached — can be built from
    /// <paramref name="services"/> or from a parameterless constructor, throwing a directed error
    /// otherwise. A policy is otherwise constructed on the first request that reaches its source,
    /// where one DI cannot supply and reflection cannot construct is a 500 on every query of that
    /// source after a startup that passed. Called once by <c>MapScry</c>; safe for any host.
    /// </summary>
    public void EnsurePoliciesResolvable(IServiceProvider services)
    {
        foreach (var source in schema.Sources)
        {
            foreach (var use in source.Policies)
            {
                if (use.Instance is null)
                {
                    EnsureResolvable(services, use.Policy, "Row policy", source.Name);
                }
            }

            if (source.AttachmentPolicy is { } attachment)
            {
                EnsureResolvable(services, attachment, "Attachment policy", source.Name);
            }
        }

        foreach (var registration in schema.CachedPolicies)
        {
            EnsureResolvable(services, registration.Policy, "Cached row policy", registration.Entity.Name);
        }

        // Checked whether or not commands are served: a capability asks its command's policy on every
        // query that reads it.
        foreach (var command in schema.Commands)
        {
            if (command.Policy is { } policy)
            {
                EnsureResolvable(services, policy, "Command policy", command.Name);
            }
        }
    }

    static void EnsureResolvable(IServiceProvider services, Type policy, string kind, string source)
    {
        if (services.GetService(policy) is not null ||
            policy.GetConstructor(Type.EmptyTypes) is not null)
        {
            return;
        }

        throw new($"{kind} '{policy.Name}' on '{source}' cannot be constructed: it is not registered with the service provider and has no parameterless constructor. Register it — services.AddScoped<{policy.Name}>() — or give it one.");
    }

    /// <summary>
    /// Translates every navigation that steps into a row-policied source, so a policy that does not
    /// compose in correlated-subquery position fails at startup rather than on the first client to
    /// name the member. Called once by <c>MapScry</c> unless
    /// <see cref="ScryOptions.ProbePoliciedNavigations"/> is cleared.
    /// </summary>
    public void ProbePoliciedNavigations(DbContext data, IServiceProvider services) =>
        executor.ProbeNavigationPolicies(data, services);

    /// <summary>
    /// Everything a host has to establish before it serves a query, in one call: the annotations match
    /// the live model, every source is mapped, every policy can be constructed and composes where it
    /// is applied. Throws a directed error for the first that does not hold.
    /// </summary>
    /// <param name="services">
    /// The host's root services. A scope is made from them for the checks that need a context, and
    /// they are where a change backplane comes from.
    /// </param>
    /// <remarks>
    /// What <c>MapScry</c> runs at startup, and what any other way of serving the same queries has to
    /// run too — a hub, a gRPC service. They are the difference between a misconfiguration that fails
    /// the deployment and one that fails a caller, and between a policy that was proved to apply and
    /// one that was assumed to. In one method so that two transports cannot come to check different
    /// things.
    /// </remarks>
    public void EnsureReady(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);
        ValidateAgainstModel(db);
        EnsureSourcesMapped(db);
        EnsureCommandTargetsMapped(db);
        EnsurePoliciesResolvable(scope.ServiceProvider);
        EnsureCommandsDispatchable(scope.ServiceProvider);
        if (options.ProbePoliciedNavigations)
        {
            ProbePoliciedNavigations(db, scope.ServiceProvider);
            ProbeCommandPolicies(db, scope.ServiceProvider);
        }

        // The model is what says which root a reported type's rows are read through, and the root
        // provider is where a backplane comes from. Both are in hand here and neither is later: a
        // host may report a change before anything has saved through the interceptor.
        Changes.Attach(db.Model);
        Changes.Attach(services);

        // Found now rather than on the first answer, so an audit that was asked for and has nowhere
        // to record fails the deployment instead of every caller.
        disclosure?.Attach(services);
    }

    /// <summary>Builds a processor from configuration (e.g. for tests or non-DI hosting).</summary>
    public static ScryProcessor Create<TContext>(Action<ScryOptions> configure)
        where TContext : DbContext
    {
        var options = new ScryOptions(typeof(TContext));
        configure(options);
        return new(Schema.Build(options), options);
    }

    /// <summary>Validates and executes a request, returning the shaped result.</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data, IServiceProvider services) =>
        Execute(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>Fetches one attachment's bytes, authorized by the source's attachment policy.</summary>
    public ScryAttachmentResult FetchAttachment(AttachmentRequest request, DbContext data, IServiceProvider services) =>
        FetchAttachment(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>
    /// Fetches one attachment's bytes, exposing <paramref name="requestHeaders"/> to the attachment
    /// policy and letting it write to <paramref name="responseHeaders"/>.
    /// </summary>
    /// <remarks>
    /// The choke point for the attachment endpoint, as <see cref="Execute(QueryRequest, DbContext, IServiceProvider)"/>
    /// is for queries: the policy runs here, the row is read through its source's row policies, and the
    /// fetch is audited — so another transport gets all three by calling this rather than reaching for
    /// the database itself. A refusal is not distinguished from a missing row; see
    /// <see cref="ScryAttachmentResult"/>.
    /// </remarks>
    public ScryAttachmentResult FetchAttachment(
        AttachmentRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.StartAttachment(schema, options, request, services);
        DisclosureCapture? capture = null;
        try
        {
            capture = Disclose(request, request.Root, caller: null, services, responseHeaders, alone: true);
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Disclosure = capture
            };
            var result = executor.FetchAttachment(request, data, scope);

            // On record before it is handed over. A fetch that found nothing sent nothing, so there
            // is nothing to record: refused, absent and hidden stay one answer here too.
            if (capture is not null &&
                result.Found)
            {
                capture.Commit();
                recorder.Disclosure = capture.Event;
            }

            // Rows are 1 for a value handed over and 0 for everything withheld, which keeps a run of
            // refusals visible in the metrics without saying which kind of refusal it was.
            recorder.Succeeded(ResultKind.Single, result.Found ? 1 : 0);
            capture?.Released();
            return result;
        }
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }
        finally
        {
            capture?.Dispose();
        }
    }

    /// <summary>
    /// The attachment fetch the HTTP endpoint runs: the same decision, with the database awaited
    /// rather than blocked on.
    /// </summary>
    internal async ValueTask<ScryAttachmentResult> FetchAttachmentAsync(
        AttachmentRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        Cancel cancel,
        string? caller = null)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.StartAttachment(schema, options, request, services);
        DisclosureCapture? capture = null;
        try
        {
            capture = Disclose(request, request.Root, caller, services, responseHeaders, alone: true);
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Disclosure = capture
            };
            var result = await executor.FetchAttachmentAsync(request, data, scope, cancel);
            if (capture is not null &&
                result.Found)
            {
                await capture.CommitAsync(cancel);
                recorder.Disclosure = capture.Event;
            }

            recorder.Succeeded(ResultKind.Single, result.Found ? 1 : 0);
            capture?.Released();
            return result;
        }
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                RequiresBody = exception.RequiresBody,
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }
        finally
        {
            if (capture is not null)
            {
                await capture.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Validates and executes a request, exposing <paramref name="requestHeaders"/> to row policies and
    /// letting them write to <paramref name="responseHeaders"/>.
    /// </summary>
    /// <remarks>
    /// The HTTP endpoint passes the live <see cref="HttpContext"/> dictionaries, so a policy's writes
    /// are already on the response by the time it is sent. Another transport can pass a
    /// <see cref="HeaderDictionary"/> of its own and do what it likes with what comes back.
    /// </remarks>
    public QueryResponse Execute(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders) =>
        Execute(request, data, services, requestHeaders, responseHeaders, binary: null);


    /// <summary>
    /// Applies what the model marks <c>[Sensitive]</c> to this request: refusing it where a constant
    /// compared against such a member arrived in a URL, and marking the response unstorable where one
    /// is returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves answer the two ways such a value escapes, and only the second is enforceable on
    /// its own. A URL is logged by every hop before it reaches here, so refusing it cannot unsay the
    /// first one — what refusing does is keep the answer from being cached under that URL, and make a
    /// client that got the choice wrong say so out loud rather than keep getting it wrong. The client
    /// reads the same rule off the same walk, so a request that reaches this is one whose sender was
    /// stale, hand-written, or lying.
    /// </para>
    /// <para>
    /// The message says only what to do. Naming the member would answer "which of these columns is the
    /// sensitive one?" for anyone willing to ask, and the attachment endpoint already collapses its own
    /// refusals for the same reason. What a developer needs is the analyzer, where the query is
    /// written.
    /// </para>
    /// </remarks>
    void ApplySensitivity(QueryRequest request, IHeaderDictionary responseHeaders, bool fromUrl)
    {
        var use = SensitiveWalk.Inspect(request, schema.Sensitive.IsSensitive);
        if (fromUrl && use.InConstant)
        {
            throw new ScryValidationException("This query compares a value against a member the model marks sensitive, so it must be sent as a request body rather than in a URL.")
            {
                RequiresBody = true
            };
        }

        // Not `private, no-cache`, which still stores: the rows are on the caller's disk either way and
        // outlive the session that asked for them. `no-store` is the only directive that says do not
        // keep this, and it is set here rather than at the endpoint because what is being returned is
        // not known until the request has been read.
        if (use.InProjection)
        {
            responseHeaders.CacheControl = "no-store";
        }
    }

    // The HTTP endpoints pass a collector so [BinaryTransfer] values leave as multipart parts; the
    // public overloads leave it null, so every non-HTTP consumer keeps today's inline base64.
    internal QueryResponse Execute(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary,
        bool fromUrl = false,
        string? caller = null,
        string? correlation = null)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, options, request, services);
        DisclosureCapture? capture = null;
        try
        {
            ApplySensitivity(request, responseHeaders, fromUrl);
            capture = Disclose(request, request.Root, caller, services, responseHeaders, correlation);
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Binary = binary,
                Disclosure = capture
            };
            var response = executor.Execute(request, data, scope) with
            {
                // Carried on every response, not only a drifted one: this is the signal a client uses
                // to notice drift in the first place, and it is the only such channel for a transport
                // that is not HTTP (which also advertises it as a response header).
                Stamp = schema.Stamp
            };

            // A drifted client may have been generated before an enum value rename, in which case the
            // payload carries names it does not know. The aliases let its reader resolve them; a
            // matching (or absent) stamp proves the client already has the current names, so nothing
            // is sent in the common case.
            if (drifted && schema.EnumAliases.Count > 0)
            {
                response = response with
                {
                    EnumAliases = schema.EnumAliases
                };
            }

            // On record before it is reported, and before it is returned: the sink holding the record
            // is what lets the answer go, and an answer it refused is reported as the failure it is.
            if (capture is not null)
            {
                capture.Commit();
                recorder.Disclosure = capture.Event;
            }

            recorder.Succeeded(response);
            capture?.Released();
            return response;
        }
        // A rejected query from a client that was generated against a different model surface is far
        // more likely stale than hostile; say so, instead of leaving an unexplained rejection. A
        // matching stamp (or none) reports the plain validation message.
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (ScryPermissionException exception)
        {
            // Ahead of the catch below so a denial is not counted as the server having broken: the
            // query was fine, and the rows it asked for were not this caller's to read.
            Denied(capture, recorder, exception);
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }
        finally
        {
            // Settles what the record is owed: nothing where the answer went or was never accepted,
            // and a withdrawal where it was accepted and an auditor then failed the request.
            capture?.Dispose();
        }
    }

    /// <summary>
    /// Executes like <see cref="Execute(QueryRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary)"/>,
    /// but writes the result into <paramref name="output"/> as complete response bytes, returning null.
    /// The one result that does not go that way is the rare drifted-client envelope carrying the enum
    /// alias table, which is returned instead for the caller to serialize the general way. Rejections
    /// and failures throw exactly as <c>Execute</c> does.
    /// </summary>
    /// <remarks>
    /// <paramref name="spill"/> is what may let a large result stop being resident; null keeps the
    /// whole envelope buffered, which is what the batch's per-entry buffer needs.
    /// <para>
    /// With the disclosure audit on nothing is returned at all: the drifted client's envelope is
    /// written into <paramref name="output"/> here as well, so that it is whole before the record of
    /// it is accepted. <paramref name="caller"/> is who that record is kept under.
    /// </para>
    /// </remarks>
    internal async ValueTask<QueryResponse?> TryExecuteBufferedAsync(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        IBufferWriter<byte> output,
        ResponseSpill? spill = null,
        BinaryPartCollector? binary = null,
        Cancel cancel = default,
        bool fromUrl = false,
        SubscriptionRun? subscription = null,
        string? caller = null,
        string? correlation = null)
    {
        var drifted = request.Stamp is { } requestStamp &&
                      requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, options, request, services, subscribed: subscription is not null);
        DisclosureCapture? capture = null;

        // Set where a live query's run has left its record and its report to whoever decides whether
        // the answer is sent. From there they are that caller's to settle, and are not settled here.
        var parked = false;
        try
        {
            ApplySensitivity(request, responseHeaders, fromUrl);
            capture = Disclose(request, request.Root, caller, services, responseHeaders, correlation);
            capture?.Subscribed = subscription is not null;
            var scope = new CallScope(services, requestHeaders, responseHeaders)
            {
                Binary = binary,
                Subscription = subscription,
                Disclosure = capture
            };

            // The alias table is carried on the envelope only for a drifted client; that rare envelope keeps
            // the fully-general path rather than teaching the writer a second shape.
            if (drifted && schema.EnumAliases.Count > 0)
            {
                var fallback = await executor.ExecuteAsync(request, data, scope, cancel) with
                {
                    Stamp = schema.Stamp,
                    EnumAliases = schema.EnumAliases
                };
                if (capture is null)
                {
                    recorder.Succeeded(fallback);
                    return fallback;
                }

                // Written here rather than handed back for the caller to write, as it is where the
                // audit is off. A transport's size limit refuses a response as it is written, and a
                // refusal has to come before the record of the answer is accepted, never after it.
                ResponseWriter.Write(output, fallback);
                if (subscription is not null)
                {
                    subscription.Capture = capture;
                    subscription.Recorder = recorder;
                    subscription.Fallback = fallback;
                    parked = true;
                    return null;
                }

                await capture.CommitAsync(cancel);
                recorder.Disclosure = capture.Event;
                recorder.Succeeded(fallback);
                capture.Released();
                return null;
            }

            var (kind, rows) = await executor.ExecuteBufferedAsync(request, data, scope, schema.Stamp, output, spill, cancel);

            // A live query's answer is sent only if it differs from the last one, which is decided
            // after this returns. So neither the record nor the report is made here: both wait with
            // the run, for the one who compares.
            if (capture is not null &&
                subscription is not null)
            {
                subscription.Capture = capture;
                subscription.Recorder = recorder;
                subscription.Kind = kind;
                subscription.Rows = rows;
                parked = true;
                return null;
            }

            // On record before it is reported, and before the transport is handed the last of it.
            if (capture is not null)
            {
                await capture.CommitAsync(cancel);
                recorder.Disclosure = capture.Event;
            }

            recorder.Succeeded(kind, rows);
            capture?.Released();
            return null;
        }
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (ScryPermissionException exception)
        {
            await DeniedAsync(capture, recorder, exception, cancel);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Reading the rows asynchronously is what makes a client disconnect land here rather than
            // at the final write, and an abandoned request is not a query that failed. Ahead of the
            // catch below so it is not counted as one.
            capture?.Abandoned();
            recorder.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }
        finally
        {
            // Settles what the record is owed: nothing where the answer went or was never accepted, a
            // close where part of it had already left, and a withdrawal where it was accepted whole
            // and an auditor then failed the request.
            if (capture is not null &&
                !parked)
            {
                await capture.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The SQL a request would run, without running it. Resolves the <see cref="DbContext"/> from
    /// <paramref name="services"/>.
    /// </summary>
    public string ToQueryString(QueryRequest request, IServiceProvider services) =>
        ToQueryString(request, (DbContext)services.GetRequiredService(options.ContextType), services);

    /// <summary>The SQL a request would run, without running it.</summary>
    public string ToQueryString(QueryRequest request, DbContext data, IServiceProvider services) =>
        ToQueryString(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>
    /// Validates a request, applies its row policies, rebinds it onto EF — then reads back the SQL
    /// instead of executing it. Everything a query is subject to has already happened, so the SQL shown
    /// is the SQL that would run, policy predicates included, and no request survives here that would
    /// have been rejected as a query.
    /// </summary>
    /// <remarks>
    /// This is a debugging aid, and the SQL reveals more than a result does — real table and column
    /// names, and the shape of any <see cref="IReturnablePolicy{T}"/> that narrowed the query. Treat it
    /// as privileged: the explorer keeps it behind a Development-only guard of its own.
    /// <para>
    /// Only a row-returning query has SQL to show. A terminal that folds the rows to a value is
    /// answered by executing it, so one is refused rather than run.
    /// </para>
    /// </remarks>
    public string ToQueryString(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders)
    {
        // The SQL says more than rows do — table and column names, the shape of every policy that
        // narrowed the query, the values bound into it — so who was shown it is recorded like any
        // other answer.
        using var capture = Disclose(request, request.Root, caller: null, services, responseHeaders);
        capture?.Begin(ScryDisclosureKind.SqlPreview);
        var rows = executor.Build(
            request,
            data,
            new(services, requestHeaders, responseHeaders)
            {
                Disclosure = capture
            });

        // Checked before asking, not after: EF's ToQueryString decides by executing the query and
        // inspecting what comes back, and for an in-memory source that means actually running it. It
        // then reports the mismatch by *returning* an explanatory sentence rather than throwing, which
        // would otherwise be handed back as though it were SQL.
        if (rows.Rows.Provider is not IAsyncQueryProvider)
        {
            throw new ScryValidationException(
                $"No SQL is available for source '{request.Root}': it is not backed by the database (a [QueryablePoco] source is supplied in memory).");
        }

        var sql = rows.Rows.ToQueryString();
        if (capture is not null)
        {
            capture.AddUnit(ScryDisclosureContentKind.Sql, Encoding.UTF8.GetBytes(sql));
            capture.Commit();
            capture.Released();
        }

        return sql;
    }

    /// <summary>Validates and executes a batch without a service provider (no DI-resolved policies).</summary>
    public QueryBatchResponse ExecuteBatch(QueryBatchRequest request, DbContext data) =>
        ExecuteBatch(request, data, EmptyServiceProvider.Instance);

    /// <summary>Validates and executes every entry of a batch, returning one result each.</summary>
    public QueryBatchResponse ExecuteBatch(QueryBatchRequest request, DbContext data, IServiceProvider services) =>
        ExecuteBatch(request, data, services, new HeaderDictionary(), new HeaderDictionary());

    /// <summary>
    /// Validates and executes every entry of a batch. Entries are independent: each goes through the
    /// same validation, row policies, and telemetry a single query does, and one that is rejected or
    /// fails is reported in its own result rather than failing the batch.
    /// </summary>
    /// <remarks>
    /// Entries run sequentially against the one <see cref="DbContext"/> — which is not thread-safe, and
    /// which a batch has no reason to work around: what a batch saves is round-trips, not database
    /// time. It is not a transaction either, so an entry that fails leaves the entries before it
    /// answered. Only the batch envelope can fail the call: an unsupported wire version, or more
    /// entries than <see cref="ScryOptions.MaxBatchSize"/>, is rejected whole and before any entry runs.
    /// </remarks>
    public QueryBatchResponse ExecuteBatch(
        QueryBatchRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders) =>
        ExecuteBatch(request, data, services, requestHeaders, responseHeaders, binary: null);

    // One collector threads through every entry, which is what numbers a batch's parts globally.
    internal QueryBatchResponse ExecuteBatch(
        QueryBatchRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary,
        string? caller = null)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = QueryRecorder.StartBatch(request.Queries.Count);
        RejectUnusableBatch(request, services, activity, started);

        var results = new List<QueryBatchResult>(request.Queries.Count);
        var correlation = Correlate();
        foreach (var query in request.Queries)
        {
            results.Add(ExecuteEntry(query, data, services, requestHeaders, responseHeaders, binary, caller, Correlated(correlation, results.Count)));
        }

        return QueryBatchResponse.Create(results) with {Stamp = schema.Stamp};
    }

    /// <summary>
    /// Executes a batch like <see cref="ExecuteBatch(QueryBatchRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary)"/>,
    /// but writes the whole envelope into <paramref name="output"/> — every entry that is rows written
    /// straight from the projected values rather than through dictionaries and a
    /// <see cref="JsonElement"/> that the envelope around it would then serialize a second time.
    /// Byte-identical to serializing what <c>ExecuteBatch</c> returns, which the golden tests pin.
    /// </summary>
    /// <remarks>
    /// Only an envelope failure throws; a rejected or failed entry is written as its own result exactly
    /// as <c>ExecuteBatch</c> reports one.
    /// <para>
    /// <paramref name="budget"/> counts what the entries carry — each one's result and parts — and not
    /// the envelope around them. An entry is checked against it as it is written into its own buffer
    /// and charged as it is copied into the envelope, so one that would cross it is refused as its own
    /// result rather than failing the entries already answered, and, having charged nothing, leaves the
    /// budget to the entries after it. That refusal is not counted either: an entry the budget turned
    /// away has to be able to say so.
    /// </para>
    /// </remarks>
    internal async Task ExecuteBatchBufferedAsync(
        QueryBatchRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        IBufferWriter<byte> output,
        BinaryPartCollector? binary,
        ResponseSpill? spill = null,
        ResponseBudget? budget = null,
        Cancel cancel = default,
        string? caller = null)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = QueryRecorder.StartBatch(request.Queries.Count);
        RejectUnusableBatch(request, services, activity, started);

        // Granted once, before the first entry runs — the only point at which a batch can decide. Its
        // parts are numbered globally and its envelope arrives last, so an entry that drained would be
        // betting that no later entry produces a part the drained bytes should have preceded. Only a
        // model with no binary member anywhere makes that bet safe.
        spill?.AllowSpill(!schema.CarriesBinary);

        // One scratch buffer for the whole batch, reset per entry rather than rented per entry, so a
        // batch of n entries rents once and settles at the width of its largest.
        using var entry = new PooledBufferWriter();
        await using var json = new Utf8JsonWriter(output);
        ResponseWriter.BeginBatch(json);
        var correlation = Correlate();
        var index = 0;
        foreach (var query in request.Queries)
        {
            await WriteEntryAsync(json, entry, query, data, services, requestHeaders, responseHeaders, binary, budget, caller, Correlated(correlation, index++), cancel);

            // Between entries, never inside one: an entry is written to a buffer of its own and inserted
            // whole precisely so a failure part-way through its rows is still reported as that entry's
            // own result, which nothing already on the wire could be replaced by.
            if (spill?.ShouldDrain(json.BytesPending) == true)
            {
                await json.FlushAsync(cancel);
                await spill.DrainAsync(cancel);
            }
        }

        ResponseWriter.EndBatch(json, schema.Stamp);
        await json.FlushAsync(cancel);
    }

    // The envelope-level rejections, which are the only way a batch fails as a whole: they are checked
    // before any entry runs, so a rejected batch has executed nothing.
    /// <summary>
    /// Refuses a batch whole — a wire version this server does not speak, or more entries than
    /// <see cref="ScryOptions.MaxBatchSize"/> — before any entry runs. Recorded as one rejection,
    /// since no entry will be recorded: the refusal would otherwise leave no metric, span, or audit
    /// entry at all.
    /// </summary>
    void RejectUnusableBatch(QueryBatchRequest request, IServiceProvider services, Activity? activity, long started)
    {
        string? reason = null;
        if (request.Version is < 1 or > WireFormat.Version)
        {
            reason = $"Unsupported wire version {request.Version}.";
        }
        else if (request.Queries.Count > options.MaxBatchSize)
        {
            reason = $"The batch carries {request.Queries.Count} queries, more than the maximum of {options.MaxBatchSize}.";
        }

        if (reason is null)
        {
            return;
        }

        var exception = new ScryValidationException(reason);
        QueryRecorder.RejectedBatch(request, exception, services, activity, Stopwatch.GetElapsedTime(started));
        throw exception;
    }

    // One entry, written rather than returned — the buffered counterpart of ExecuteEntry, and its
    // catches must stay identical to that one's.
    //
    // The entry is written into a buffer of its own first and only inserted once it is whole: an entry
    // can fail part-way through its rows (the database is read as they are written), and a writer
    // already mid-array cannot take back what it has written to report the failure in its place.
    async Task WriteEntryAsync(
        Utf8JsonWriter json,
        PooledBufferWriter entry,
        QueryRequest query,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary,
        ResponseBudget? budget,
        string? caller,
        string? correlation,
        Cancel cancel)
    {
        entry.Reset();

        try
        {
            // No spill: an entry is inserted into the envelope only once it is whole, which is the
            // whole reason it is written into a buffer of its own.
            var output = budget?.Checking(entry) ?? entry;
            var fallback = await TryExecuteBufferedAsync(
                query,
                data,
                services,
                requestHeaders,
                responseHeaders,
                output,
                spill: null,
                binary,
                cancel,
                caller: caller,
                correlation: correlation);

            // The writer declined this one, so the buffer is untouched and the envelope is serialized
            // into it — which makes it an entry like any other: checked as it is written, charged once
            // it is whole, and inserted the one way.
            if (fallback is not null)
            {
                ResponseWriter.Write(output, fallback);
            }

            budget?.Spend(entry.WrittenCount);
        }
        catch (ScryValidationException exception)
        {
            ResponseWriter.WriteEntry(json, exception.Message, HttpStatusCode.BadRequest, ErrorCodes.Classify(exception));
            return;
        }
        catch (ScryPermissionException exception)
        {
            // Per entry, like a rejection: one entry's rows being denied says nothing about the
            // others', and a batch that failed whole would make a denial impossible to attribute.
            ResponseWriter.WriteEntry(json, exception.Message, HttpStatusCode.Forbidden, ScryErrorCode.Forbidden);
            return;
        }
        catch (OperationCanceledException)
        {
            // The one place this diverges from ExecuteEntry, which reads its rows synchronously and so
            // cannot be abandoned part-way. Nobody is left to read a per-entry failure, and the entries
            // after this one have nobody to answer either, so the batch goes with it.
            throw;
        }
        catch (Exception)
        {
            // A drifted client faulting the server is far more likely stale than the server broken,
            // the same attribution the single-query endpoint makes for an execution failure.
            ResponseWriter.WriteEntry(
                json,
                "Query execution failed.",
                HttpStatusCode.InternalServerError,
                ErrorCodes.Failed(query.Stamp is { } stamp && stamp != schema.Stamp));
            return;
        }

        ResponseWriter.WriteEntry(json, entry.WrittenMemory.Span);
    }

    // One entry, reported rather than thrown. The catches mirror the HTTP endpoint's: a validation
    // message is the client's own doing and is safe to return, and anything else is the fixed text a
    // 500 carries, so batching an entry never reveals more than sending it alone would. WriteEntry
    // above is the buffered counterpart and must report an entry exactly as this does.
    QueryBatchResult ExecuteEntry(
        QueryRequest query,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary,
        string? caller,
        string? correlation)
    {
        try
        {
            return new()
            {
                Response = Execute(query, data, services, requestHeaders, responseHeaders, binary, caller: caller, correlation: correlation)
            };
        }
        catch (ScryValidationException exception)
        {
            return new()
            {
                Error = exception.Message,
                Status = HttpStatusCode.BadRequest,
                Code = ErrorCodes.Classify(exception)
            };
        }
        catch (ScryPermissionException exception)
        {
            return new()
            {
                Error = exception.Message,
                Status = HttpStatusCode.Forbidden,
                Code = ScryErrorCode.Forbidden
            };
        }
        catch (Exception)
        {
            return new()
            {
                Error = "Query execution failed.",
                Status = HttpStatusCode.InternalServerError,
                // A drifted client faulting the server is far more likely stale than the server broken,
                // the same attribution the single-query endpoint makes for an execution failure.
                Code = ErrorCodes.Failed(query.Stamp is { } stamp && stamp != schema.Stamp)
            };
        }
    }

    /// <summary>
    /// Validates a request and returns its rows as a stream rather than a materialized result, plus the
    /// opening marker a transport writes before them.
    /// </summary>
    /// <remarks>
    /// Validation has run to completion by the time this returns — a rejected query never reaches EF —
    /// so a transport can commit to a success status before pulling the first row. A failure after that
    /// point is the provider's, and belongs in the stream's closing marker rather than a status code.
    /// </remarks>
    public (ScryStreamMarker Begin, IAsyncEnumerable<Dictionary<string, object?>> Rows) Stream(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        Cancel cancel = default) =>
        Stream(request, data, services, new HeaderDictionary(), new HeaderDictionary(), cancel);

    /// <summary>
    /// Streams a request, exposing <paramref name="requestHeaders"/> to row policies and letting them
    /// write to <paramref name="responseHeaders"/>.
    /// </summary>
    /// <remarks>
    /// Policies run while the query is built, which is before this returns — so a policy's writes are
    /// in hand while a transport can still send headers, rather than after the response has started.
    /// </remarks>
    public (ScryStreamMarker Begin, IAsyncEnumerable<Dictionary<string, object?>> Rows) Stream(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        Cancel cancel = default)
    {
        var (begin, rows, recorder, capture) = StreamCore(request, data, services, requestHeaders, responseHeaders);
        if (capture is null)
        {
            return (begin, Shape(rows, options.MaxStreamRows, recorder, cancel));
        }

        // Held back a chunk at a time until the record of each is accepted.
        return (begin, RecordedRows(rows, options.MaxStreamRows, recorder, capture, options.Disclosure!.StreamChunkBytes, cancel));
    }

    /// <summary>
    /// Streams like <see cref="Stream(QueryRequest, DbContext, IServiceProvider, IHeaderDictionary, IHeaderDictionary, Cancel)"/>,
    /// but each row arrives as its finished JSON bytes, written by the plan's shape writer — the
    /// buffer is valid until the next row is pulled.
    /// </summary>
    internal (ScryStreamMarker Begin, bool Binary, IAsyncEnumerable<ReadOnlyMemory<byte>> Rows) StreamBuffered(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        Cancel cancel = default,
        BinaryPartCollector? binary = null,
        ResponseBudget? budget = null,
        string? caller = null)
    {
        var (begin, rows, recorder, capture) = StreamCore(request, data, services, requestHeaders, responseHeaders, binary, caller);
        // Whether any row can divert — known from the plan before the first byte, which is what lets
        // the transport commit to a multipart content type up front, data-independently.
        var diverting = binary is not null && rows.Plan.BinarySlots is not null;
        return (begin, diverting, Streamed(rows, budget, recorder, capture, cancel));
    }

    // A stream's lines: as they are read, or — where the disclosure audit is on — held back a chunk at
    // a time until the record of each chunk is accepted.
    IAsyncEnumerable<ReadOnlyMemory<byte>> Streamed(
        QueryExecutor.RowSet rows,
        ResponseBudget? budget,
        QueryRecorder recorder,
        DisclosureCapture? capture,
        Cancel cancel)
    {
        if (capture is null)
        {
            return Lines(rows, options.MaxStreamRows, budget, recorder, cancel);
        }

        return RecordedLines(rows, options.MaxStreamRows, budget, recorder, capture, options.Disclosure!.StreamChunkBytes, cancel);
    }

    /// <summary>
    /// <see cref="StreamBuffered"/> with what comes before the first row — the policies brought up
    /// to date, the probes — awaited rather than blocked on. What the HTTP endpoint runs.
    /// </summary>
    internal async ValueTask<(ScryStreamMarker Begin, bool Binary, IAsyncEnumerable<ReadOnlyMemory<byte>> Rows)> StreamBufferedAsync(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        Cancel cancel = default,
        BinaryPartCollector? binary = null,
        ResponseBudget? budget = null,
        string? caller = null)
    {
        var drifted = request.Stamp is { } requestStamp && requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, options, request, services, streamed: true);
        QueryExecutor.RowSet rows;
        DisclosureCapture? capture = null;

        // Whether the record has been handed to the stream that will fill it. Until it is, anything
        // that ends this call ends the record with it.
        var streaming = false;
        try
        {
            capture = Disclose(request, request.Root, caller, services, responseHeaders);
            capture?.Begin(ScryDisclosureKind.Stream);
            rows = await executor.StreamAsync(
                request,
                data,
                new(services, requestHeaders, responseHeaders)
                {
                    Binary = binary,
                    Disclosure = capture
                },
                cancel);

            // A stream of nothing but what the host left out of the record is not recorded, and is
            // sent as it would be with the audit off rather than held back a chunk at a time.
            if (capture is {Recording: null})
            {
                await capture.DisposeAsync();
                capture = null;
            }

            streaming = true;
        }
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                RequiresBody = exception.RequiresBody,
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (ScryPermissionException exception)
        {
            await DeniedAsync(capture, recorder, exception, cancel);
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }
        finally
        {
            if (capture is not null &&
                !streaming)
            {
                await capture.DisposeAsync();
            }
        }

        var diverting = binary is not null && rows.Plan.BinarySlots is not null;
        return (Begin(drifted), diverting, Streamed(rows, budget, recorder, capture, cancel));
    }

    (ScryStreamMarker Begin, QueryExecutor.RowSet Rows, QueryRecorder Recorder, DisclosureCapture? Capture) StreamCore(
        QueryRequest request,
        DbContext data,
        IServiceProvider services,
        IHeaderDictionary requestHeaders,
        IHeaderDictionary responseHeaders,
        BinaryPartCollector? binary = null,
        string? caller = null)
    {
        var drifted = request.Stamp is { } requestStamp && requestStamp != schema.Stamp;
        var recorder = QueryRecorder.Start(schema, options, request, services, streamed: true);
        QueryExecutor.RowSet rows;
        DisclosureCapture? capture = null;
        var streaming = false;
        try
        {
            capture = Disclose(request, request.Root, caller, services, responseHeaders);
            capture?.Begin(ScryDisclosureKind.Stream);
            rows = executor.Stream(request, data, new(services, requestHeaders, responseHeaders)
            {
                Binary = binary,
                Disclosure = capture
            });
            if (capture is {Recording: null})
            {
                capture.Dispose();
                capture = null;
            }

            streaming = true;
        }
        catch (ScryValidationException exception) when (drifted)
        {
            var stale = new ScryValidationException($"{exception.Message} The request's schema stamp does not match this server's model, so the client was generated against a different model surface — regenerate the client.")
            {
                // Kept through the rewrite: a stale client's refusal is still one it can act on
                // immediately by re-sending in a body, whatever it does about regenerating.
                RequiresBody = exception.RequiresBody,
                StaleClient = true
            };
            recorder.Rejected(stale);
            throw stale;
        }
        catch (ScryValidationException exception)
        {
            recorder.Rejected(exception);
            throw;
        }
        catch (ScryPermissionException exception)
        {
            // Thrown while the stream was being built, which is before its first byte — so a denial
            // still answers as a status rather than as an error marker mid-response.
            Denied(capture, recorder, exception);
            throw;
        }
        catch (Exception exception)
        {
            recorder.Failed(exception);
            throw;
        }
        finally
        {
            if (!streaming)
            {
                capture?.Dispose();
            }
        }

        return (Begin(drifted), rows, recorder, capture);
    }

    ScryStreamMarker Begin(bool drifted) =>
        new()
        {
            Kind = ScryStream.Begin,
            Version = WireFormat.Version,
            Stamp = schema.Stamp,
            EnumAliases = drifted && schema.EnumAliases.Count > 0 ? schema.EnumAliases : null
        };

    static async IAsyncEnumerable<Dictionary<string, object?>> Shape(
        QueryExecutor.RowSet rows,
        int? maxRows,
        QueryRecorder recorder,
        [EnumeratorCancellation] Cancel cancel)
    {
        await foreach (var row in Raw(rows, maxRows, recorder, cancel))
        {
            yield return QueryExecutor.ShapeRow(row, rows);
        }
    }

    // One writer and one buffer serve the whole stream: each row overwrites the last, which is why
    // the yielded memory is only valid until the next pull — exactly how the transport consumes it.
    // The buffer is pooled, so the memory is also only valid until the enumeration ends, which is the
    // same moment by the time the transport has written the row out.
    static async IAsyncEnumerable<ReadOnlyMemory<byte>> Lines(
        QueryExecutor.RowSet rows,
        int? maxRows,
        ResponseBudget? budget,
        QueryRecorder recorder,
        [EnumeratorCancellation] Cancel cancel)
    {
        var writer = rows.Plan.Writer;
        var buffer = new PooledBufferWriter();
        Utf8JsonWriter? json = null;
        try
        {
            await foreach (var row in Raw(rows, maxRows, recorder, cancel))
            {
                buffer.Reset();
                if (json is null)
                {
                    json = new(buffer);
                }
                else
                {
                    json.Reset(buffer);
                }

                WriteLine(writer, json, buffer, ResponseWriter.Row(row, rows), rows.Binary, budget, recorder);
                yield return buffer.WrittenMemory;
            }
        }
        finally
        {
            if (json != null)
            {
                await json.DisposeAsync();
            }

            buffer.Dispose();
        }
    }

    // Apart from Lines because a catch cannot hold a yield. The line is spent with its newline, which
    // is part of what the transport sends for it; a row's binary parts were spent as it collected them.
    // Recorded here because Raw cannot see the refusal: it happens after Raw has handed the row over.
    static void WriteLine(
        PlanShapeWriter writer,
        Utf8JsonWriter json,
        PooledBufferWriter buffer,
        object[] row,
        BinaryPartCollector? binary,
        ResponseBudget? budget,
        QueryRecorder recorder)
    {
        try
        {
            writer.WriteRow(json, row, binary);
            json.Flush();
            budget?.Spend(buffer.WrittenCount + 1);
        }
        catch (ScryValidationException exception)
        {
            // Thrown rather than returned, as MaxStreamRows is: the transport turns it into the stream's
            // error marker, so the client sees a truncated result as a failure.
            recorder.Rejected(exception);
            throw;
        }
    }

    static async IAsyncEnumerable<object> Raw(
        QueryExecutor.RowSet rows,
        int? maxRows,
        QueryRecorder recorder,
        [EnumeratorCancellation] Cancel cancel)
    {
        var count = 0;
        var enumerator = QueryExecutor.Enumerate(rows, cancel).GetAsyncEnumerator(cancel);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    recorder.Canceled(count);
                    throw;
                }
                catch (Exception exception)
                {
                    recorder.Failed(exception);
                    throw;
                }

                if (!moved)
                {
                    break;
                }

                if (count++ == maxRows)
                {
                    // Thrown rather than returned: the transport turns it into the stream's error marker,
                    // so the client sees a truncated result as a failure rather than as the end of the data.
                    var truncated = new ScryValidationException($"The query returned more than the maximum of {maxRows} streamed rows.");
                    recorder.Rejected(truncated);
                    throw truncated;
                }

                yield return enumerator.Current;
            }

            recorder.Succeeded(count);
        }
        finally
        {
            // A consumer that stops reading ends the stream here, with no completion of its own. The
            // first completion wins inside the recorder, so on every fully-reported path this no-ops.
            recorder.Canceled(count);
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>Streams a request without a service provider (no DI-resolved policies).</summary>
    public (ScryStreamMarker Begin, IAsyncEnumerable<Dictionary<string, object?>> Rows) Stream(
        QueryRequest request,
        DbContext data,
        Cancel cancel = default) =>
        Stream(request, data, EmptyServiceProvider.Instance, cancel);

    /// <summary>Executes a request without a service provider (no DI-resolved policies).</summary>
    public QueryResponse Execute(QueryRequest request, DbContext data) =>
        Execute(request, data, EmptyServiceProvider.Instance);

    /// <summary>Probes without a service provider, for a host that has no DI to resolve one from.</summary>
    public void ProbePoliciedNavigations(DbContext data) =>
        ProbePoliciedNavigations(data, EmptyServiceProvider.Instance);

    /// <summary>
    /// Fetches an attachment without a service provider. The attachment policy still runs — it is
    /// constructed directly when DI has no answer — so this is unauthorized only if the policy is.
    /// </summary>
    public ScryAttachmentResult FetchAttachment(AttachmentRequest request, DbContext data) =>
        FetchAttachment(request, data, EmptyServiceProvider.Instance);
}
