using System.Runtime.ExceptionServices;

namespace Scry;

public sealed partial class ScryProcessor
{
    // The disclosure audit as this processor runs it, or null where the host never turned it on —
    // which is the one check every answer makes, and all the audit costs a host without it.
    DisclosureRuntime? disclosure;

    // The schema as it is sent, written once: it is the same document for as long as the processor
    // lives, and a caller reading it is recorded as having read exactly this.
    byte[]? described;

    /// <summary>
    /// Starts the record of one answer, where the disclosure audit is on, and marks the response as
    /// one no cache may keep. Null where the audit is off.
    /// </summary>
    /// <remarks>
    /// Both in one step because they are one decision. A copy a cache keeps is read again with no
    /// request at all — by the same caller later, or by whoever uses that browser profile next — and
    /// a read with no request is one nothing here can record. <c>private, no-cache</c> would not do:
    /// it revalidates before reuse, and still stores.
    /// </remarks>
    DisclosureCapture? Disclose(
        object request,
        string source,
        string? caller,
        IServiceProvider services,
        IHeaderDictionary responseHeaders,
        string? correlation = null,
        bool alone = false)
    {
        if (disclosure is null)
        {
            return null;
        }

        // A source the host left out of the record. What reads its rows and nothing else — an
        // attachment of one — is not recorded. A query rooted at it may go on to read a source that
        // is recorded, which is known only once it has been walked: so its record is begun, and
        // what every recorded answer is owed waits until then.
        if (schema.TryGetSource(source, out var root) &&
            disclosure.Settings.Excludes(root.ClrType))
        {
            if (alone)
            {
                return null;
            }

            var undecided = disclosure.BeginUndecided(request, source, caller, services, responseHeaders);
            undecided.Correlation = correlation;
            return undecided;
        }

        responseHeaders.CacheControl = "no-store";
        var capture = disclosure.Begin(request, source, caller, services);
        capture.Correlation = correlation;
        return capture;
    }

    /// <summary>
    /// What ties the answers of one request together in the record: the entries of a batch, the
    /// answers of a live query. Null where the audit is off, so nothing is made for nobody to read.
    /// </summary>
    string? Correlate()
    {
        if (disclosure is null)
        {
            return null;
        }

        return Guid.CreateVersion7().ToString("N");
    }

    static string? Correlated(string? correlation, int index)
    {
        if (correlation is null)
        {
            return null;
        }

        return $"{correlation}/{index.ToString(CultureInfo.InvariantCulture)}";
    }

    // A caller refused because a row it may not read matched has been told that such a row exists.
    // That is recorded before it is said, like everything else: with no content, since none was sent.
    // A sink that will not take it turns the refusal into a failure, which says nothing.
    static void Denied(DisclosureCapture? capture, QueryRecorder recorder, ScryPermissionException exception)
    {
        if (capture is not null)
        {
            try
            {
                capture.Deny();
                recorder.Disclosure = capture.Event;
            }
            catch (Exception refused)
            {
                recorder.Failed(refused);
                throw;
            }
        }

        recorder.Denied(exception);
    }

    static async ValueTask DeniedAsync(DisclosureCapture? capture, QueryRecorder recorder, ScryPermissionException exception, Cancel cancel)
    {
        if (capture is not null)
        {
            try
            {
                await capture.DenyAsync(cancel);
                recorder.Disclosure = capture.Event;
            }
            catch (Exception refused)
            {
                recorder.Failed(refused);
                throw;
            }
        }

        recorder.Denied(exception);
    }

    /// <summary>
    /// Describes the allow-listed query surface to a caller, recording that they were sent it where
    /// the disclosure audit is on. What a transport serves a schema through.
    /// </summary>
    /// <param name="services">The request's services, which say who is asking where <paramref name="caller"/> does not.</param>
    /// <param name="caller">Who is asking, where the transport knows.</param>
    /// <remarks>
    /// The schema is no row of anybody's, but it is the map of everything there is to ask for: the
    /// sources, their members, which of them are marked sensitive. Who was handed it is part of who
    /// knew what. <see cref="Describe()"/> records nothing, and is for a host reading its own surface.
    /// </remarks>
    public ScryIntrospection Describe(IServiceProvider services, string? caller = null)
    {
        var introspection = schema.Describe(options);
        if (disclosure is null)
        {
            return introspection;
        }

        using var capture = disclosure.Begin(request: null, source: "", caller, services);
        capture.Begin(ScryDisclosureKind.Schema);
        capture.AddUnit(ScryDisclosureContentKind.Schema, described ??= Encoding.UTF8.GetBytes(ScryJson.Serialize(introspection)));
        capture.Commit();
        capture.Released();
        return introspection;
    }

    /// <summary>
    /// Records that somebody read the disclosure record, before they are shown what they asked for.
    /// Reading who saw what is itself seeing it, so whatever shows the record to a person records
    /// each question through the sink the answers went through, and shows nothing where that is not
    /// accepted.
    /// </summary>
    /// <param name="question">What was asked.</param>
    /// <param name="reviewer">Who asked.</param>
    /// <param name="parameters">What the question was asked about, as canonical JSON. Kept by address, as content is.</param>
    /// <param name="results">How many results the answer holds.</param>
    /// <param name="events">
    /// The events whose content the answer shows: an event opened, a result exported. Empty for an
    /// answer that lists who and when and shows none. From then on the reviewer is among those who
    /// received the rows of each.
    /// </param>
    /// <param name="services">The request's services.</param>
    /// <param name="cancel">Stops waiting for the sink.</param>
    /// <exception cref="ScryDisclosureException">
    /// The audit is off, nobody could be named and anonymous callers are not allowed, or the sink did
    /// not accept the record. In each case nothing should be shown.
    /// </exception>
    public async ValueTask<ScryDisclosureReview> ReviewAsync(
        ScryDisclosureQuestion question,
        string? reviewer,
        ReadOnlyMemory<byte> parameters,
        int results,
        IReadOnlyList<Guid> events,
        IServiceProvider services,
        Cancel cancel = default)
    {
        if (disclosure is null)
        {
            throw new ScryDisclosureException($"The disclosure audit is off, so there is no record for a review to be added to. {nameof(ScryOptions)}.{nameof(ScryOptions.UseDisclosureAudit)} turns it on.");
        }

        if (reviewer is null &&
            !disclosure.Settings.AllowAnonymous)
        {
            throw new ScryDisclosureException($"Nobody could be named as reading the disclosure record, so nothing of it was shown. Set {nameof(ScryDisclosureOptions)}.{nameof(ScryDisclosureOptions.AllowAnonymous)} where it may be read by nobody in particular.");
        }

        ScryDisclosureAddress? asked = null;
        IReadOnlyList<ScryDisclosureContent> contents = [];
        if (!parameters.IsEmpty)
        {
            // Addressed as content is: its kind, then its bytes, under the same key.
            var tagged = new byte[parameters.Length + 1];
            tagged[0] = (byte) ScryDisclosureContentKind.Parameters;
            parameters.Span.CopyTo(tagged.AsSpan(1));
            var address = disclosure.Address(tagged);
            asked = address;
            contents = [new(address, ScryDisclosureContentKind.Parameters, parameters.Length, parameters)];
        }

        var review = new ScryDisclosureReview(Guid.CreateVersion7(), disclosure.Now, question)
        {
            Reviewer = reviewer,
            Parameters = asked,
            Results = results,
            Events = events,
            Node = disclosure.Settings.Node
        };
        await disclosure.AppendAsync(
            new()
            {
                EventId = review.Id,
                Review = review,
                Contents = contents
            },
            services,
            cancel);
        return review;
    }

    // One receipt, recorded before it is handed on: where a command stood, what it answered with,
    // and the row it was sent against.
    async ValueTask DiscloseReceipt(CommandRecord record, CommandReceipt receipt, string? caller, IServiceProvider services, Cancel cancel)
    {
        if (disclosure is null)
        {
            return;
        }

        var source = "";
        if (record.Meta.Target is { } target)
        {
            // A receipt says what became of a command against a row, and a row of a source the
            // host left out of the record is not one anybody is asked after.
            if (disclosure.Settings.Excludes(target.ClrType))
            {
                return;
            }

            source = DisclosurePlanner.SourceName(schema, target.ClrType);
        }

        await using var capture = disclosure.Begin(record.Request, source, caller, services);
        capture.Begin(ScryDisclosureKind.CommandReceipt);
        if (record.DisclosureKey is { } key)
        {
            capture.AddEntity(slot: 0, source, key, via: "target");
        }

        capture.AddUnit(ScryDisclosureContentKind.Receipt, ScryJson.SerializeToUtf8(receipt));
        await capture.CommitAsync(cancel);
        capture.Released();
    }

    // What becomes of one run of a live query, once it is known whether its answer is to be sent.
    // Sent, it is recorded first and then reported; not sent, it is reported with nothing recorded,
    // since nothing left. Either way the run is audited once, as every run is.
    static async ValueTask Settle(SubscriptionRun run, bool sent, ScryDisclosureDelivery delivery, Cancel cancel)
    {
        if (run.Capture is not { } capture)
        {
            return;
        }

        var recorder = run.Recorder!;
        try
        {
            if (sent)
            {
                capture.Delivery = delivery;
                await capture.CommitAsync(cancel);
                recorder.Disclosure = capture.Event;
            }

            if (run.Fallback is { } fallback)
            {
                recorder.Succeeded(fallback);
            }
            else
            {
                recorder.Succeeded(run.Kind, run.Rows);
            }

            capture.Released();
        }
        catch (OperationCanceledException)
        {
            capture.Abandoned();
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
            await capture.DisposeAsync();
        }
    }

    /// <summary>
    /// A stream's rows as they may be handed on with the disclosure audit on: held back until the
    /// record of them is accepted, a chunk at a time, and then let go in the order they were read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows are held in one of two ways. A row with no binary value is written once, as the line it
    /// will be sent as, and that line is what is recorded and what is handed on. A row that has one is
    /// held as its values: its record names the value by digest, so it is written a second time when
    /// it is handed on — one row at a time, as a stream always wrote them, so that each row's binary
    /// parts are collected just before its own line and numbered from zero.
    /// </para>
    /// <para>
    /// A read that ends early — the row limit, the size limit, the database failing — ends after the
    /// rows already read have been recorded and handed on, so the rows that precede the error are the
    /// ones that preceded it before any of this existed.
    /// </para>
    /// </remarks>
    sealed class RecordedStream(
        QueryExecutor.RowSet rows,
        int? maxRows,
        ResponseBudget? budget,
        QueryRecorder recorder,
        DisclosureCapture capture,
        int chunkBytes,
        bool lines,
        Cancel cancel) :
        IAsyncDisposable
    {
        IAsyncEnumerator<object> source = QueryExecutor.Enumerate(rows, cancel).GetAsyncEnumerator(cancel);
        ProjectionPlan plan = rows.Plan;

        // Whether a row's line is its record: a stream of lines whose rows hold no binary value.
        bool written = lines && !rows.Plan.HasBytes;
        PooledBufferWriter line = new();
        PooledBufferWriter chunk = new();
        Utf8JsonWriter? json;
        List<(int Offset, int Length)> slices = [];
        List<object[]> held = [];
        int pulled;
        int handed;
        bool finished;
        bool settled;
        Exception? failure;

        /// <summary>How many rows of the chunk last accepted are waiting to be handed on.</summary>
        public int Held
        {
            get
            {
                if (written)
                {
                    return slices.Count;
                }

                return held.Count;
            }
        }

        /// <summary>A held row as the line it is sent as. Valid until the next chunk is read.</summary>
        public ReadOnlyMemory<byte> Line(int index)
        {
            var (offset, length) = slices[index];
            return chunk.WrittenMemory.Slice(offset, length);
        }

        /// <summary>A held row as its values, for a caller that writes it itself.</summary>
        public object[] Row(int index) =>
            held[index];

        /// <summary>Whether every row of the stream holds no binary value, so its line is held ready.</summary>
        public bool Written => written;

        /// <summary>Says one more row has been handed on.</summary>
        public void Handed() =>
            capture.Handed(++handed);

        /// <summary>
        /// Reads the next chunk of rows and has the record of them accepted. False once there are no
        /// more: the read reached its end, or ended early and the rows read before that are out.
        /// </summary>
        public async ValueTask<bool> NextAsync()
        {
            slices.Clear();
            held.Clear();
            chunk.Reset();
            if (finished ||
                failure is not null)
            {
                return false;
            }

            var size = 0;
            while (true)
            {
                if (await Pull() is not { } row)
                {
                    break;
                }

                size += Hold(row);
                if (failure is not null ||
                    size >= chunkBytes)
                {
                    break;
                }
            }

            if (Held == 0)
            {
                return false;
            }

            // The caller has gone: there is nobody to hand the rows to, so nothing to record of them.
            if (failure is OperationCanceledException)
            {
                slices.Clear();
                held.Clear();
                return false;
            }

            try
            {
                await capture.FlushAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The rows are not handed on, and the stream ends as the failure it is.
                settled = true;
                recorder.Failed(exception);
                throw;
            }

            recorder.Disclosure = capture.Event;
            return true;
        }

        // The next row, or null at the end of the read — its real end, or one a limit or a fault made.
        async ValueTask<object[]?> Pull()
        {
            bool moved;
            try
            {
                moved = await source.MoveNextAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
                return null;
            }

            if (!moved)
            {
                finished = true;
                return null;
            }

            if (pulled++ == maxRows)
            {
                // The transport turns it into the stream's error marker, so the client sees a truncated
                // result as a failure rather than as the end of the data.
                failure = new ScryValidationException($"The query returned more than the maximum of {maxRows} streamed rows.");
                return null;
            }

            return ResponseWriter.Row(source.Current, rows);
        }

        // Takes a row into the chunk and records it. Returns how many bytes it added.
        int Hold(object[] row)
        {
            if (!written)
            {
                held.Add(row);
                return capture.AddRow(plan, row);
            }

            // Written first, to learn its length before it is committed to: a row the size limit
            // refuses is neither recorded nor sent.
            line.Reset();
            if (json is null)
            {
                json = new(line);
            }
            else
            {
                json.Reset(line);
            }

            plan.Writer.WriteRow(json, row);
            json.Flush();
            try
            {
                // With its newline, which is part of what the transport sends for it.
                budget?.Spend(line.WrittenCount + 1);
            }
            catch (ScryValidationException exception)
            {
                failure = exception;
                return 0;
            }

            var bytes = line.WrittenMemory.Span;
            capture.AddWritten(plan, row, bytes);
            slices.Add((chunk.WrittenCount, bytes.Length));
            bytes.CopyTo(chunk.GetSpan(bytes.Length));
            chunk.Advance(bytes.Length);
            return bytes.Length;
        }

        /// <summary>Says the read was ended by something that happened while a row was being handed on.</summary>
        public void Fail(Exception exception) =>
            failure ??= exception;

        /// <summary>
        /// Ends the stream once every row there was to hand on has been: records how it ended, reports
        /// it, and throws what ended it early where something did.
        /// </summary>
        public async ValueTask CompleteAsync()
        {
            settled = true;
            if (failure is null)
            {
                try
                {
                    await capture.EndAsync(ScryDisclosureOutcome.Released, handed, cancel);
                }
                catch (Exception exception)
                {
                    recorder.Failed(exception);
                    throw;
                }

                recorder.Disclosure = capture.Event;
                recorder.Succeeded(handed);
                return;
            }

            var outcome = failure switch
            {
                OperationCanceledException => ScryDisclosureOutcome.Canceled,
                ScryValidationException => ScryDisclosureOutcome.Truncated,
                _ => ScryDisclosureOutcome.Failed
            };
            try
            {
                // Not the request's token: a caller that went away is the commonest way to get here.
                await capture.EndAsync(outcome, handed, Cancel.None);
                recorder.Disclosure = capture.Event;
            }
            catch (Exception)
            {
                // The failure being reported is the one that ended the read. An event left without
                // its close reads as "may have been sent", for the rows the sink already holds.
            }

            switch (failure)
            {
                case OperationCanceledException:
                    recorder.Canceled(handed);
                    break;
                case ScryValidationException rejected:
                    recorder.Rejected(rejected);
                    break;
                default:
                    recorder.Failed(failure);
                    break;
            }

            ExceptionDispatchInfo.Throw(failure);
        }

        public async ValueTask DisposeAsync()
        {
            // A consumer that stops reading ends the stream here, with no completion of its own. The
            // first completion wins inside the recorder, so on every fully-reported path this no-ops.
            if (!settled)
            {
                capture.Abandoned();
            }

            recorder.Canceled(handed);
            await capture.DisposeAsync();
            await source.DisposeAsync();
            if (json is not null)
            {
                await json.DisposeAsync();
            }

            line.Dispose();
            chunk.Dispose();
        }
    }

    // The stream endpoint's lines, with the audit on.
    static async IAsyncEnumerable<ReadOnlyMemory<byte>> RecordedLines(
        QueryExecutor.RowSet rows,
        int? maxRows,
        ResponseBudget? budget,
        QueryRecorder recorder,
        DisclosureCapture capture,
        int chunkBytes,
        [EnumeratorCancellation] Cancel cancel)
    {
        await using var stream = new RecordedStream(rows, maxRows, budget, recorder, capture, chunkBytes, lines: true, cancel);
        var writer = rows.Plan.Writer;
        var buffer = new PooledBufferWriter();
        Utf8JsonWriter? json = null;
        try
        {
            while (await stream.NextAsync())
            {
                for (var index = 0; index < stream.Held; index++)
                {
                    if (stream.Written)
                    {
                        stream.Handed();
                        yield return stream.Line(index);
                        continue;
                    }

                    // Written now, one row at a time, so that the binary parts collected are this
                    // row's and are drained before the next row is written.
                    buffer.Reset();
                    if (json is null)
                    {
                        json = new(buffer);
                    }
                    else
                    {
                        json.Reset(buffer);
                    }

                    if (!Wire(stream, writer, json, buffer, stream.Row(index), rows.Binary, budget))
                    {
                        break;
                    }

                    stream.Handed();
                    yield return buffer.WrittenMemory;
                }
            }

            await stream.CompleteAsync();
        }
        finally
        {
            if (json is not null)
            {
                await json.DisposeAsync();
            }

            buffer.Dispose();
        }
    }

    // Apart from RecordedLines because a catch cannot hold a yield. False where the size limit
    // refused the row, which ends the stream once the rows before it are out.
    static bool Wire(
        RecordedStream stream,
        PlanShapeWriter writer,
        Utf8JsonWriter json,
        PooledBufferWriter buffer,
        object[] row,
        BinaryPartCollector? binary,
        ResponseBudget? budget)
    {
        try
        {
            writer.WriteRow(json, row, binary);
            json.Flush();
            budget?.Spend(buffer.WrittenCount + 1);
            return true;
        }
        catch (ScryValidationException exception)
        {
            stream.Fail(exception);
            return false;
        }
    }

    // The programmatic stream's rows, with the audit on.
    static async IAsyncEnumerable<Dictionary<string, object?>> RecordedRows(
        QueryExecutor.RowSet rows,
        int? maxRows,
        QueryRecorder recorder,
        DisclosureCapture capture,
        int chunkBytes,
        [EnumeratorCancellation] Cancel cancel)
    {
        await using var stream = new RecordedStream(rows, maxRows, budget: null, recorder, capture, chunkBytes, lines: false, cancel);
        while (await stream.NextAsync())
        {
            for (var index = 0; index < stream.Held; index++)
            {
                stream.Handed();
                yield return QueryExecutor.ShapeValues(stream.Row(index), rows);
            }
        }

        await stream.CompleteAsync();
    }
}
