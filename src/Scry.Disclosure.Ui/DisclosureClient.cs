/// <summary>
/// The page's side of the conversation written down in <c>DisclosureContract</c>: each question sent
/// to the server that served the page, and its answer or the reason there was none.
/// </summary>
/// <remarks>
/// Nothing here throws for an answer that did not come. A question that is refused, or that never
/// reached the server, is a thing the page shows in place of the answer, so it comes back as one.
/// </remarks>
sealed class DisclosureClient(HttpClient http)
{
    public Task<Reply<CatalogAnswer>> Catalog(Cancel cancel = default) =>
        Get("api/catalog", DisclosureJson.Default.CatalogAnswer, cancel);

    public Task<Reply<RowAnswer>> Rows(RowQuestion question, Cancel cancel = default) =>
        Post("api/rows", question, DisclosureJson.Default.RowQuestion, DisclosureJson.Default.RowAnswer, cancel);

    public Task<Reply<EventsAnswer>> Callers(CallerQuestion question, Cancel cancel = default) =>
        Post("api/callers", question, DisclosureJson.Default.CallerQuestion, DisclosureJson.Default.EventsAnswer, cancel);

    public Task<Reply<EventsAnswer>> Members(MemberQuestion question, Cancel cancel = default) =>
        Post("api/members", question, DisclosureJson.Default.MemberQuestion, DisclosureJson.Default.EventsAnswer, cancel);

    public Task<Reply<EventAnswer>> Event(Guid id, Cancel cancel = default) =>
        Get($"api/events/{id:D}", DisclosureJson.Default.EventAnswer, cancel);

    public Task<Reply<StatusAnswer>> Status(Cancel cancel = default) =>
        Get("api/status", DisclosureJson.Default.StatusAnswer, cancel);

    public Task<Reply<CheckLine>> Verify(VerifyQuestion question, Cancel cancel = default) =>
        Post("api/verify", question, DisclosureJson.Default.VerifyQuestion, DisclosureJson.Default.CheckLine, cancel);

    public Task<Reply<ErasureLine>> Erase(EraseQuestion question, Cancel cancel = default) =>
        Post("api/erase", question, DisclosureJson.Default.EraseQuestion, DisclosureJson.Default.ErasureLine, cancel);

    /// <summary>A question asked again for all of its answer, as a file's bytes.</summary>
    public async Task<Reply<ExportedFile>> Export(ExportQuestion question, Cancel cancel = default)
    {
        try
        {
            using var response = await http.PostAsync("api/export", JsonContent.Create(question, DisclosureJson.Default.ExportQuestion), cancel);
            if (!response.IsSuccessStatusCode)
            {
                return new(null, await Refused(response, cancel));
            }

            var name = response.Content.Headers.ContentDisposition?.FileNameStar ??
                       response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ??
                       $"disclosures.{question.Format}";
            var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            return new(new(name, type, bytes, response.Headers.Contains("Scry-Export-Cut")), null);
        }
        catch (HttpRequestException)
        {
            return new(null, unreachable);
        }
    }

    const string unreachable = "The server could not be reached.";

    async Task<Reply<T>> Get<T>(string path, JsonTypeInfo<T> type, Cancel cancel)
        where T : class
    {
        try
        {
            using var response = await http.GetAsync(path, cancel);
            return await Read(response, type, cancel);
        }
        catch (HttpRequestException)
        {
            return new(null, unreachable);
        }
    }

    async Task<Reply<TAnswer>> Post<TQuestion, TAnswer>(
        string path,
        TQuestion question,
        JsonTypeInfo<TQuestion> asked,
        JsonTypeInfo<TAnswer> answered,
        Cancel cancel)
        where TAnswer : class
    {
        try
        {
            using var response = await http.PostAsync(path, JsonContent.Create(question, asked), cancel);
            return await Read(response, answered, cancel);
        }
        catch (HttpRequestException)
        {
            return new(null, unreachable);
        }
    }

    static async Task<Reply<T>> Read<T>(HttpResponseMessage response, JsonTypeInfo<T> type, Cancel cancel)
        where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            return new(null, await Refused(response, cancel));
        }

        try
        {
            if (await response.Content.ReadFromJsonAsync(type, cancel) is { } answer)
            {
                return new(answer, null);
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Said below.
        }

        return new(null, "The server sent an answer this page could not read.");
    }

    // Why not, in the server's words where it gave any. A closed explorer answers with nothing at
    // all, so there may be none.
    static async Task<string> Refused(HttpResponseMessage response, Cancel cancel)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync(DisclosureJson.Default.Refusal, cancel) is {Message.Length: > 0} refusal)
            {
                return refusal.Message;
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Said below.
        }

        return $"The server answered {(int) response.StatusCode}, and did not say why.";
    }
}

/// <summary>An answer, or why there was none. Exactly one of the two is set.</summary>
sealed record Reply<T>(T? Value, string? Refusal)
    where T : class;

/// <summary>A result written out as a file: what to call it, what it is, and its bytes.</summary>
/// <param name="Cut">Whether it holds only the newest of what was asked for.</param>
sealed record ExportedFile(string Name, string Type, byte[] Bytes, bool Cut);
