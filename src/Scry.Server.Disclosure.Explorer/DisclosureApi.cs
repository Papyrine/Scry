/// <summary>
/// The explorer's questions, answered from the store. Each is behind the explorer's guard, each
/// answer is one no cache may keep, and each question that shows somebody's data is written to the
/// record before it is answered: reading who saw what is itself seeing it.
/// </summary>
/// <remarks>
/// The order inside every handler is the same and is the point of it: work out the answer, record
/// that it was asked for, and only then write it. A record that is not accepted means an answer that
/// is not given — the write-ahead rule the answers themselves were recorded under, applied to the
/// person reading them.
/// </remarks>
sealed class DisclosureApi(ScryDisclosureExplorerOptions options)
{
    // The most a question may be. They are a few names, a key and two dates.
    const int questionLimit = 64 * 1024;

    // The most one answer lists, whatever it was asked for.
    const int pageLimit = 200;

    // How many erasures and reviews the status view shows.
    const int recent = 50;

    public void Map(RouteGroupBuilder api)
    {
        api.MapGet("/catalog", Asked(Catalog));
        api.MapPost("/rows", Asked(Rows));
        api.MapPost("/callers", Asked(Callers));
        api.MapPost("/members", Asked(Members));
        api.MapGet("/events/{id:guid}", (HttpContext context, Guid id) => Guarded(context, _ => Event(_, id)));
        api.MapGet("/status", Asked(Status));
        api.MapPost("/verify", Asked(Verify));
        api.MapPost("/erase", Asked(Erase));
        api.MapPost("/export", Asked(Export));

        // Anything else under it is nothing, rather than the page the explorer's catch-all would serve.
        api.Map("/{**rest}", () => Results.NotFound());
    }

    // A question behind the guard, as the route handler it is mapped as. Typed as a delegate so that
    // what it returns is written as the response: a handler taking only the context would otherwise
    // be taken for a RequestDelegate, which is handed nothing back.
    Delegate Asked(Func<HttpContext, Task<IResult>> answer) =>
        (HttpContext context) => Guarded(context, answer);

    async Task<IResult> Guarded(HttpContext context, Func<HttpContext, Task<IResult>> answer)
    {
        if (!options.EnableGuard(context))
        {
            // 404 (not 403) so a closed explorer cannot be told from one that was never mapped.
            return Results.NotFound();
        }

        // An answer is somebody's data. Nothing between here and the reader keeps a copy of it, for
        // the reason the answers it describes were sent the same way: a copy is read again with no
        // request, and a read with no request is one nothing here can record.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";

        // Only the page this server sent may ask. A browser says where a request came from, and one
        // sent by another site's page — an image tag, a form — would be recorded as the reader's own
        // question, in the reader's name. A client that is not a browser sends no such header.
        if (context.Request.Headers.TryGetValue("Sec-Fetch-Site", out var from) &&
            from.ToString() is not ("same-origin" or "none"))
        {
            return Refuse(StatusCodes.Status403Forbidden, "The disclosure record is only read from its own page.");
        }

        return await answer(context);
    }

    async Task<IResult> Catalog(HttpContext context)
    {
        var services = context.RequestServices;
        var catalog = await Reader(context).Catalog(context.RequestAborted);
        var answer = new CatalogAnswer(
            [
                .. catalog.Sources.Select(
                    source => new CatalogSource(
                        source.Name,
                        source.Keyed,
                        [.. source.Members.Select(_ => new CatalogMember(_.Name, _.Sensitive))]))
            ],
            options.Reviewer(context),

            // Said so that the page offers each only where it would work, rather than a control that 404s.
            Export: options.EnableExport(context),
            Erase: options.EnableErase(context) && services.GetService<IScryDisclosureEraser>() is not null,
            Verify: services.GetService<IScryDisclosureVerifier>() is not null);
        return Results.Json(answer, DisclosureJson.Default.CatalogAnswer);
    }

    async Task<IResult> Rows(HttpContext context)
    {
        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var (question, refused) = await Read(context, DisclosureJson.Default.RowQuestion);
        if (question is null)
        {
            return refused!;
        }

        if (string.IsNullOrWhiteSpace(question.Source))
        {
            return Refuse(StatusCodes.Status400BadRequest, "A row is asked about by its source and its key.");
        }

        if (!DisclosureKeys.TryRead(question.Key, out var key))
        {
            return Refuse(StatusCodes.Status400BadRequest, DisclosureKeys.Help);
        }

        var cancel = context.RequestAborted;
        var reader = Reader(context);
        var (listed, more) = await Page(
            reader.ReceiversOf(question.Source, key, question.From, question.To, Mark(question.After), cancel),
            question.Take,
            cancel);

        var shapes = new Dictionary<ScryDisclosureAddress, IReadOnlyList<MemberLine>>();
        var lines = new List<RowLine>(listed.Count);
        foreach (var row in listed)
        {
            ReviewLine? review = null;
            if (row.Review is { } shown)
            {
                review = Line(shown);
            }

            lines.Add(
                new(
                    Line(row.Event, row.Close),
                    row.Ordinal,
                    row.Content.ToString(),
                    row.Via,
                    await Left(reader, row.Event.Shape, shapes, cancel),
                    review));
        }

        PageMark? next = null;
        if (more)
        {
            next = Mark(ScryDisclosureCursor.After(listed[^1]));
        }

        var answer = new RowAnswer(ScryDisclosureEntity.KeyOf(key), lines, next);
        return await Answer(context, ScryDisclosureQuestion.Row, question, DisclosureJson.Default.RowQuestion, answer, DisclosureJson.Default.RowAnswer, lines.Count);
    }

    async Task<IResult> Callers(HttpContext context)
    {
        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var (question, refused) = await Read(context, DisclosureJson.Default.CallerQuestion);
        if (question is null)
        {
            return refused!;
        }

        var cancel = context.RequestAborted;
        var (listed, more) = await Page(
            Reader(context).ReceivedBy(
                question.Caller,
                question.From ?? DateTimeOffset.MinValue,
                question.To ?? DateTimeOffset.MaxValue,
                Mark(question.After),
                cancel),
            question.Take,
            cancel);
        var answer = Listed(listed, more);
        return await Answer(context, ScryDisclosureQuestion.Caller, question, DisclosureJson.Default.CallerQuestion, answer, DisclosureJson.Default.EventsAnswer, listed.Count);
    }

    async Task<IResult> Members(HttpContext context)
    {
        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var (question, refused) = await Read(context, DisclosureJson.Default.MemberQuestion);
        if (question is null)
        {
            return refused!;
        }

        if (string.IsNullOrWhiteSpace(question.Source) ||
            string.IsNullOrWhiteSpace(question.Member))
        {
            return Refuse(StatusCodes.Status400BadRequest, "A member is asked about by its source and its name.");
        }

        var cancel = context.RequestAborted;
        var (listed, more) = await Page(
            Reader(context).MemberReceivedBy(question.Caller, question.Source, question.Member, Mark(question.After), cancel),
            question.Take,
            cancel);
        var answer = Listed(listed, more);
        return await Answer(context, ScryDisclosureQuestion.Member, question, DisclosureJson.Default.MemberQuestion, answer, DisclosureJson.Default.EventsAnswer, listed.Count);
    }

    static EventsAnswer Listed(List<ScryDisclosureEntry> listed, bool more)
    {
        PageMark? next = null;
        if (more)
        {
            next = Mark(ScryDisclosureCursor.After(listed[^1]));
        }

        return new([.. listed.Select(_ => Line(_.Event, _.Close))], next);
    }

    // The one question that shows content, so the one whose record names the event: from here on
    // whoever asked is among those who received its rows.
    async Task<IResult> Event(HttpContext context, Guid id)
    {
        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var cancel = context.RequestAborted;
        var found = await Reader(context).Reconstruct(id, cancel);
        var parameters = Encoding.UTF8.GetBytes($$"""{"event":"{{id:D}}"}""");
        if (found is null)
        {
            // Asked all the same, and recorded as asked: that somebody went looking is part of it.
            if (await Record(context, ScryDisclosureQuestion.Event, parameters, results: 0, events: []) is { } unrecorded)
            {
                return unrecorded;
            }

            return Refuse(StatusCodes.Status404NotFound, "The record holds no such event.");
        }

        var released = found.Close?.Units ?? int.MaxValue;
        var units = new List<UnitLine>(found.Units.Count);
        foreach (var unit in found.Units)
        {
            units.Add(
                new(
                    unit.Ordinal,
                    unit.Content.Address.ToString(),
                    unit.Content.Kind.ToString(),
                    unit.Content.Length,
                    DisclosureExporter.State(unit),
                    unit.Ordinal < released,
                    DisclosureExporter.Text(unit),
                    [.. unit.Entities.Select(_ => new EntityLine(_.Source, _.Key, _.Via))]));
        }

        string? request = null;
        if (found.Request is { } asked)
        {
            request = Encoding.UTF8.GetString(asked.Span);
        }

        IReadOnlyList<MemberLine> fields = [];
        if (found.Shape is { } shape)
        {
            fields = [.. shape.Fields.Select(Line)];
        }

        var answer = new EventAnswer(
            Line(found.Event, found.Close),
            request,
            fields,
            units,
            DisclosureExporter.Payload(found),
            found.Event.Stamp,
            found.Close?.At);
        if (await Record(context, ScryDisclosureQuestion.Event, parameters, results: 1, events: [id]) is { } refused)
        {
            return refused;
        }

        return Results.Json(answer, DisclosureJson.Default.EventAnswer);
    }

    async Task<IResult> Status(HttpContext context)
    {
        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var cancel = context.RequestAborted;
        var services = context.RequestServices;
        ScryDisclosureStoreStatus? stands = null;
        if (services.GetService<IScryDisclosureStatus>() is { } status)
        {
            stands = await status.Status(cancel);
        }

        var erasures = new List<ErasureLine>();
        if (services.GetService<IScryDisclosureEraser>() is { } eraser)
        {
            var (erased, _) = await Page(eraser.Erasures(cancel), recent, cancel);
            erasures = [.. erased.Select(Line)];
        }

        var (reviews, _) = await Page(Reader(context).Reviews(cancel: cancel), recent, cancel);
        CheckLine? verified = null;
        if (stands?.LastVerification is { } check)
        {
            verified = Line(check);
        }

        var answer = new StatusAnswer(
            stands is not null,
            stands?.Pending,
            stands?.PendingBytes,
            stands?.OldestPending,
            stands?.Events,
            stands?.ChainHead?.ToString(),
            verified,
            stands?.Problem,
            erasures,
            [.. reviews.Select(Line)]);
        if (await Record(context, ScryDisclosureQuestion.Status, parameters: default, results: 1, events: []) is { } refused)
        {
            return refused;
        }

        return Results.Json(answer, DisclosureJson.Default.StatusAnswer);
    }

    async Task<IResult> Verify(HttpContext context)
    {
        if (context.RequestServices.GetService<IScryDisclosureVerifier>() is not { } verifier)
        {
            return Refuse(StatusCodes.Status404NotFound, "This store keeps nothing of its own to check.");
        }

        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var (question, refused) = await Read(context, DisclosureJson.Default.VerifyQuestion);
        if (question is null)
        {
            return refused!;
        }

        var check = await verifier.Verify(question.From, Math.Clamp(question.Count, 1, 10_000), context.RequestAborted);
        return await Answer(context, ScryDisclosureQuestion.Verify, question, DisclosureJson.Default.VerifyQuestion, Line(check), DisclosureJson.Default.CheckLine, results: 1);
    }

    // Recorded before it is done rather than after: an erasure nobody is on record as having asked
    // for is the one thing this must never produce, so where the record will not take the question
    // nothing is erased.
    async Task<IResult> Erase(HttpContext context)
    {
        if (!options.EnableErase(context) ||
            context.RequestServices.GetService<IScryDisclosureEraser>() is not { } eraser)
        {
            return Results.NotFound();
        }

        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var (question, refused) = await Read(context, DisclosureJson.Default.EraseQuestion);
        if (question is null)
        {
            return refused!;
        }

        if (string.IsNullOrWhiteSpace(question.Source) ||
            !DisclosureKeys.TryRead(question.Key, out var key))
        {
            return Refuse(StatusCodes.Status400BadRequest, DisclosureKeys.Help);
        }

        if (!DisclosureKeys.TryRead(question.Confirm, out var again) ||
            ScryDisclosureEntity.KeyOf(again) != ScryDisclosureEntity.KeyOf(key))
        {
            return Refuse(StatusCodes.Status400BadRequest, "The key typed again is not the key. Nothing was erased.");
        }

        var parameters = JsonSerializer.SerializeToUtf8Bytes(question, DisclosureJson.Default.EraseQuestion);
        if (await Record(context, ScryDisclosureQuestion.Erase, parameters, results: 0, events: []) is { } unrecorded)
        {
            return unrecorded;
        }

        var erasure = await eraser.EraseAsync(question.Source, key, options.Reviewer(context), context.RequestAborted);
        return Results.Json(Line(erasure), DisclosureJson.Default.ErasureLine);
    }

    // A question asked again for all of its answer, with the content, as a file. Everything it is
    // about to write is named in the record before the first byte of it is.
    async Task<IResult> Export(HttpContext context)
    {
        if (!options.EnableExport(context))
        {
            return Results.NotFound();
        }

        if (Unnamed(context) is { } unnamed)
        {
            return unnamed;
        }

        var (question, refused) = await Read(context, DisclosureJson.Default.ExportQuestion);
        if (question is null)
        {
            return refused!;
        }

        if (question.Format is not ("csv" or "json"))
        {
            return Refuse(StatusCodes.Status400BadRequest, "An export is written as csv or as json.");
        }

        var cancel = context.RequestAborted;
        var reader = Reader(context);
        var gathered = await Gather(question, reader, cancel);
        if (gathered is null)
        {
            return Refuse(StatusCodes.Status400BadRequest, "An export is of one thing: a row, a caller, a member or an event.");
        }

        var (wanted, cut) = gathered.Value;
        var parameters = JsonSerializer.SerializeToUtf8Bytes(question, DisclosureJson.Default.ExportQuestion);
        if (await Record(context, ScryDisclosureQuestion.Export, parameters, wanted.Count, [.. wanted.Select(_ => _.Event)]) is { } unrecorded)
        {
            return unrecorded;
        }

        // Said in a header as well as in the file, so the page can tell the reader before they open it.
        if (cut)
        {
            context.Response.Headers["Scry-Export-Cut"] = "true";
        }

        var at = Audit(context).Clock.GetUtcNow();
        var name = $"disclosures-{at:yyyyMMdd-HHmmss}.{question.Format}";
        if (question.Format == "csv")
        {
            return Results.Stream(_ => DisclosureExporter.Csv(_, wanted, reader, cut, cancel), "text/csv; charset=utf-8", name);
        }

        return Results.Stream(_ => DisclosureExporter.Json(_, wanted, reader, cut, cancel), "application/json", name);
    }

    // The events an export is of, newest first, up to the most one export writes — and whether there
    // were more. Null where the question names nothing, or more than one thing.
    async Task<(List<ExportedEvent> Wanted, bool Cut)?> Gather(ExportQuestion question, IScryDisclosureReader reader, Cancel cancel)
    {
        var asked = 0;
        foreach (var part in new object?[] {question.Row, question.Caller, question.Member, question.Event})
        {
            if (part is not null)
            {
                asked++;
            }
        }

        if (asked != 1)
        {
            return null;
        }

        var wanted = new List<ExportedEvent>();
        if (question.Event is { } id)
        {
            if (await reader.Reconstruct(id, cancel) is not null)
            {
                wanted.Add(new(id, Units: null));
            }

            return (wanted, false);
        }

        if (question.Row is { } row)
        {
            if (string.IsNullOrWhiteSpace(row.Source) ||
                !DisclosureKeys.TryRead(row.Key, out var key))
            {
                return null;
            }

            // Only the units that carried the row, of each event that released it. The reviewers
            // since shown one of those events are part of who received the row, not of what it was.
            var units = new Dictionary<Guid, HashSet<int>>();
            await foreach (var release in reader.ReceiversOf(row.Source, key, row.From, row.To, cancel: cancel))
            {
                if (release.Review is not null)
                {
                    continue;
                }

                if (!units.TryGetValue(release.Event.Id, out var carried))
                {
                    if (wanted.Count == options.ExportLimit)
                    {
                        return (wanted, true);
                    }

                    units[release.Event.Id] = carried = [];
                    wanted.Add(new(release.Event.Id, carried));
                }

                carried.Add(release.Ordinal);
            }

            return (wanted, false);
        }

        IAsyncEnumerable<ScryDisclosureEntry> listed;
        if (question.Caller is { } caller)
        {
            listed = reader.ReceivedBy(caller.Caller, caller.From ?? DateTimeOffset.MinValue, caller.To ?? DateTimeOffset.MaxValue, cancel: cancel);
        }
        else
        {
            var member = question.Member!;
            if (string.IsNullOrWhiteSpace(member.Source) ||
                string.IsNullOrWhiteSpace(member.Member))
            {
                return null;
            }

            listed = reader.MemberReceivedBy(member.Caller, member.Source, member.Member, cancel: cancel);
        }

        await foreach (var entry in listed)
        {
            if (wanted.Count == options.ExportLimit)
            {
                return (wanted, true);
            }

            wanted.Add(new(entry.Event.Id, Units: null));
        }

        return (wanted, false);
    }

    // The question, recorded; then its answer. In that order and never the other.
    async Task<IResult> Answer<TQuestion, TAnswer>(
        HttpContext context,
        ScryDisclosureQuestion asked,
        TQuestion question,
        JsonTypeInfo<TQuestion> questionType,
        TAnswer answer,
        JsonTypeInfo<TAnswer> answerType,
        int results)
    {
        var parameters = JsonSerializer.SerializeToUtf8Bytes(question, questionType);
        if (await Record(context, asked, parameters, results, events: []) is { } refused)
        {
            return refused;
        }

        return Results.Json(answer, answerType);
    }

    // Null once the record holds the question. Otherwise the refusal to send instead of the answer:
    // what went wrong is the host's to read in its own logs, and is not said to the browser.
    async Task<IResult?> Record(HttpContext context, ScryDisclosureQuestion asked, ReadOnlyMemory<byte> parameters, int results, IReadOnlyList<Guid> events)
    {
        var processor = context.RequestServices.GetRequiredService<ScryProcessor>();
        try
        {
            await processor.ReviewAsync(asked, options.Reviewer(context), parameters, results, events, context.RequestServices, context.RequestAborted);
            return null;
        }
        catch (ScryDisclosureException)
        {
            return Refuse(StatusCodes.Status500InternalServerError, "That this was asked could not be recorded, so nothing was shown.");
        }
    }

    // Said before anything is read, rather than left for the record to refuse after the work is done.
    IResult? Unnamed(HttpContext context)
    {
        if (options.Reviewer(context) is null &&
            !Audit(context).AllowAnonymous)
        {
            return Refuse(StatusCodes.Status403Forbidden, "Nobody is named as reading the record, so nothing of it is shown. Sign in as somebody the record can name.");
        }

        return null;
    }

    static ScryDisclosureOptions Audit(HttpContext context) =>
        context.RequestServices.GetRequiredService<ScryOptions>().Disclosure!;

    static IScryDisclosureReader Reader(HttpContext context) =>
        context.RequestServices.GetRequiredService<IScryDisclosureReader>();

    static IResult Refuse(int status, string message) =>
        Results.Json(new Refusal(message), DisclosureJson.Default.Refusal, statusCode: status);

    // A question as JSON, and nothing else: a form cannot send application/json, which keeps a
    // cross-site one from being read as a question — the rule the query endpoints apply.
    static async Task<(T? Question, IResult? Refused)> Read<T>(HttpContext context, JsonTypeInfo<T> type)
        where T : class
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) ||
            !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Refuse(StatusCodes.Status415UnsupportedMediaType, "A question is sent as application/json."));
        }

        if (context.Request.ContentLength > questionLimit)
        {
            return (null, Refuse(StatusCodes.Status413PayloadTooLarge, "That is too long to be a question."));
        }

        // Read to a limit of its own, since a body need not say how long it is.
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > questionLimit)
            {
                return (null, Refuse(StatusCodes.Status413PayloadTooLarge, "That is too long to be a question."));
            }

            body.Write(buffer, 0, read);
        }

        try
        {
            if (JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, (int) body.Length), type) is { } question)
            {
                return (question, null);
            }
        }
        catch (JsonException)
        {
            // Said below, as the one thing wrong with it.
        }

        return (null, Refuse(StatusCodes.Status400BadRequest, "The question could not be read."));
    }

    // As many as were asked for, within what one answer lists, and whether there were more.
    static async Task<(List<T> Items, bool More)> Page<T>(IAsyncEnumerable<T> listed, int take, Cancel cancel)
    {
        take = Math.Clamp(take, 1, pageLimit);
        var items = new List<T>(take);
        await foreach (var item in listed.WithCancellation(cancel))
        {
            if (items.Count == take)
            {
                return (items, true);
            }

            items.Add(item);
        }

        return (items, false);
    }

    // What an event returned, read from its shape once however many of a page's rows share it.
    static async ValueTask<IReadOnlyList<MemberLine>> Left(
        IScryDisclosureReader reader,
        ScryDisclosureAddress? shape,
        Dictionary<ScryDisclosureAddress, IReadOnlyList<MemberLine>> known,
        Cancel cancel)
    {
        if (shape is not { } address)
        {
            return [];
        }

        if (known.TryGetValue(address, out var lines))
        {
            return lines;
        }

        lines = [];
        if (await reader.Shape(address, cancel) is { } found)
        {
            lines = [.. found.Fields.Where(Left).Select(Line)];
        }

        known[address] = lines;
        return lines;
    }

    // Whether something of the member left the server: its value, a value folded from it, or what it
    // says the caller may do. A member only filtered or ordered by did not, and is the event view's to show.
    static bool Left(ScryDisclosureField field) =>
        field.Use is ScryDisclosureFieldUse.Returned or ScryDisclosureFieldUse.Aggregated or ScryDisclosureFieldUse.Capability;

    static ScryDisclosureCursor? Mark(PageMark? mark)
    {
        if (mark is null)
        {
            return null;
        }

        return new(mark.At, mark.Id, mark.Ordinal);
    }

    static PageMark Mark(ScryDisclosureCursor cursor) =>
        new(cursor.At, cursor.Id, cursor.Ordinal);

    static EventLine Line(ScryDisclosureEvent header, ScryDisclosureClose? close) =>
        new(
            header.Id,
            header.At,
            header.Caller,
            header.Kind.ToString(),
            header.Source,
            header.Subscribed,
            header.Delivery.ToString(),
            header.Sensitive,
            close?.Outcome.ToString(),
            close?.Units,
            header.Node,
            header.Correlation,
            header.ContentType);

    static MemberLine Line(ScryDisclosureField field) =>
        new(field.Source, field.Member, field.Use.ToString(), field.Sensitive);

    static ReviewLine Line(ScryDisclosureReview review) =>
        new(review.Id, review.At, review.Reviewer, review.Question.ToString(), review.Results, review.Events.Count);

    static ErasureLine Line(ScryDisclosureErasure erasure) =>
        new(erasure.At, erasure.By, erasure.Source, erasure.Key, erasure.Units);

    static CheckLine Line(ScryDisclosureChainCheck check) =>
        new(check.At, check.Records, check.Intact, check.BrokenAt);
}

/// <summary>One event an export writes, and which of its units where it is not all of them.</summary>
sealed record ExportedEvent(Guid Event, HashSet<int>? Units);
