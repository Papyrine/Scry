/// <summary>
/// Writes events out of the record as a file, and says what one unit carried as text — which the
/// event view and both file formats all have to agree on.
/// </summary>
static class DisclosureExporter
{
    /// <summary>
    /// Whether the store holds a unit's bytes: <c>held</c>, <c>erased</c> where an erasure removed
    /// them, or <c>digest</c> where only their digest and length were ever recorded.
    /// </summary>
    public static string State(ScryDisclosedUnit unit)
    {
        if (unit.Erased)
        {
            return "erased";
        }

        if (unit.Content.Held)
        {
            return "held";
        }

        return "digest";
    }

    /// <summary>
    /// What a unit carried, as text, where the store holds it and it is text. Null for one that was
    /// erased, one recorded by digest alone, and binary content, which has no text to show.
    /// </summary>
    public static string? Text(ScryDisclosedUnit unit)
    {
        if (unit.Erased ||
            !unit.Content.Held ||
            unit.Content.Kind == ScryDisclosureContentKind.Bytes)
        {
            return null;
        }

        return Encoding.UTF8.GetString(unit.Content.Bytes.Span);
    }

    /// <summary>
    /// What left, put back together from the store: the rows of a list as the array they were sent
    /// as, one row or a scalar as itself, anything else as the text it was.
    /// </summary>
    /// <remarks>
    /// Only what was released. A unit whose bytes the store no longer holds, or never did, takes a
    /// marker's place in it rather than vanishing: an answer of three rows is still shown as three.
    /// </remarks>
    public static string Payload(ScryDisclosedResponse answer)
    {
        var released = answer.Close?.Units ?? int.MaxValue;
        var sent = answer.Units.Where(_ => _.Ordinal < released).ToList();
        switch (answer.Event.Kind)
        {
            case ScryDisclosureKind.List:
            case ScryDisclosureKind.Page:
            case ScryDisclosureKind.Stream:
                return $"[{string.Join(',', sent.Select(Sent))}]";
            case ScryDisclosureKind.Single:
                if (sent.Count == 0)
                {
                    return "null";
                }

                return Sent(sent[0]);
            default:
                return string.Join('\n', sent.Select(Sent));
        }
    }

    // One unit's place in a payload: what it carried, or what is known of it where that is gone.
    static string Sent(ScryDisclosedUnit unit)
    {
        if (Text(unit) is { } text)
        {
            return text;
        }

        if (unit.Erased)
        {
            return $$"""{"$erased":true,"length":{{unit.Content.Length}}}""";
        }

        return $$"""{"$bytes":"{{unit.Content.Address}}","length":{{unit.Content.Length}}}""";
    }

    // What a unit carried, for a cell or a property: its text, or in its place what became of it.
    static string Shown(ScryDisclosedUnit unit)
    {
        if (Text(unit) is { } text)
        {
            return text;
        }

        if (unit.Erased)
        {
            return $"(erased, was {unit.Content.Length} bytes)";
        }

        if (unit.Content.Held)
        {
            return $"(binary, {unit.Content.Length} bytes)";
        }

        return $"(not kept, {unit.Content.Length} bytes, {unit.Content.Address})";
    }

    /// <summary>A line for each unit, under a header: what a spreadsheet opens.</summary>
    public static async Task Csv(Stream output, IReadOnlyList<ExportedEvent> wanted, IScryDisclosureReader reader, bool cut, Cancel cancel)
    {
        // With a byte-order mark, which is what tells a spreadsheet the file is UTF-8 and not the
        // machine's own code page: names in the record are whatever names are.
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await writer.WriteLineAsync("When,Caller,Kind,Source,Event,Outcome,Unit,Released,Rows,Content");
        foreach (var exported in wanted)
        {
            if (await reader.Reconstruct(exported.Event, cancel) is not { } answer)
            {
                continue;
            }

            var released = answer.Close?.Units ?? int.MaxValue;
            foreach (var unit in Units(answer, exported))
            {
                string[] cells =
                [
                    answer.Event.At.ToString("O", CultureInfo.InvariantCulture),
                    answer.Event.Caller ?? "",
                    answer.Event.Kind.ToString(),
                    answer.Event.Source,
                    answer.Event.Id.ToString("D"),
                    Outcome(answer),
                    unit.Ordinal.ToString(CultureInfo.InvariantCulture),
                    Flag(unit.Ordinal < released),
                    string.Join("; ", unit.Entities.Select(Row)),
                    Shown(unit)
                ];
                await writer.WriteLineAsync(string.Join(',', cells.Select(Cell)));
            }
        }

        if (cut)
        {
            await writer.WriteLineAsync(Cell($"(cut short: only the newest {wanted.Count} events are here)"));
        }
    }

    /// <summary>The same as one document, with each event's request beside its units.</summary>
    public static async Task Json(Stream output, IReadOnlyList<ExportedEvent> wanted, IScryDisclosureReader reader, bool cut, Cancel cancel)
    {
        await using var json = new Utf8JsonWriter(
            output,
            new()
            {
                Indented = true,

                // A file somebody opens, not text set into a page: a quote inside what a unit carried
                // is written as a quote, so that a row reads as the row it is.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        json.WriteStartObject();
        json.WriteBoolean("cut", cut);
        json.WriteStartArray("events");
        foreach (var exported in wanted)
        {
            if (await reader.Reconstruct(exported.Event, cancel) is not { } answer)
            {
                continue;
            }

            var released = answer.Close?.Units ?? int.MaxValue;
            json.WriteStartObject();
            json.WriteString("id", answer.Event.Id);
            json.WriteString("at", answer.Event.At);
            json.WriteString("caller", answer.Event.Caller);
            json.WriteString("kind", answer.Event.Kind.ToString());
            json.WriteString("source", answer.Event.Source);
            json.WriteString("outcome", Outcome(answer));
            if (answer.Request is { } request)
            {
                json.WriteString("request", request.Span);
            }

            json.WriteStartArray("units");
            foreach (var unit in Units(answer, exported))
            {
                json.WriteStartObject();
                json.WriteNumber("unit", unit.Ordinal);
                json.WriteBoolean("released", unit.Ordinal < released);
                json.WriteString("state", State(unit));
                json.WriteStartArray("rows");
                foreach (var entity in unit.Entities)
                {
                    json.WriteStringValue(Row(entity));
                }

                json.WriteEndArray();
                json.WriteString("content", Text(unit));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();

            // An event at a time, so that an export of many is never all held at once.
            await json.FlushAsync(cancel);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    // The units of an event an export writes: all of them, or — for an export of one row — the ones
    // that carried it.
    static IEnumerable<ScryDisclosedUnit> Units(ScryDisclosedResponse answer, ExportedEvent exported)
    {
        if (exported.Units is not { } carried)
        {
            return answer.Units;
        }

        return answer.Units.Where(_ => carried.Contains(_.Ordinal));
    }

    static string Outcome(ScryDisclosedResponse answer)
    {
        if (answer.Close is not { } close)
        {
            return "never closed";
        }

        return close.Outcome.ToString();
    }

    static string Flag(bool value)
    {
        if (value)
        {
            return "yes";
        }

        return "no";
    }

    static string Row(ScryDisclosureEntity entity)
    {
        if (entity.Via.Length == 0)
        {
            return $"{entity.Source}{entity.Key}";
        }

        return $"{entity.Source}{entity.Key} via {entity.Via}";
    }

    // One field, quoted where it has to be — and made text where a spreadsheet would run it. A cell
    // beginning with '=', '+', '-', '@', a tab or a carriage return is read as a formula, and a
    // formula can reach outside the sheet; what is being written here is whatever was in the rows,
    // and whatever a caller called itself. So such a cell is led with an apostrophe, which every
    // spreadsheet takes as "text follows" and none displays. A number is left alone: "-5" is a value
    // to go on computing with, and no formula is a number. The rule the query explorer's exporter
    // applies, for the reason it gives.
    internal static string Cell(string value)
    {
        if (value.Length > 0 &&
            value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
