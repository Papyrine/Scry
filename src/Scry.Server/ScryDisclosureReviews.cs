namespace Scry;

/// <summary>
/// Records what somebody reading the disclosure record was shown, before they are shown it: the
/// question, who asked it, how many answers it had, and which events it opened. Reading who saw
/// what is itself seeing it.
/// </summary>
/// <remarks>
/// <para>
/// Needs a sink and the audit's settings, and nothing of a model: so the record can be read from a
/// host that serves no queries. <c>AddScry</c> registers one where the audit is on, and
/// <see cref="ScryServiceExtensions.AddScryDisclosureRecord"/> where the host only reads.
/// </para>
/// <para>
/// The settings that matter here are <see cref="ScryDisclosureOptions.AllowAnonymous"/>,
/// <see cref="ScryDisclosureOptions.AddressKey"/>, <see cref="ScryDisclosureOptions.Node"/> and
/// <see cref="ScryDisclosureOptions.Clock"/>. The key addresses a question's parameters, and a host
/// with another key records the same question under another address: nothing reads wrongly for it,
/// but one question is then two pieces of content.
/// </para>
/// </remarks>
public sealed class ScryDisclosureReviews(IScryDisclosureSink sink, ScryDisclosureOptions settings)
{
    /// <summary>The settings reviews are recorded under.</summary>
    public ScryDisclosureOptions Settings => settings;

    /// <summary>Records one question, and returns it as recorded.</summary>
    /// <param name="question">What was asked.</param>
    /// <param name="reviewer">Who asked it, or null where nobody can be named.</param>
    /// <param name="parameters">What it was asked about, as the asker's own bytes; kept as content.</param>
    /// <param name="results">How many answers it was given.</param>
    /// <param name="events">The events whose content it shows, each of which the reviewer becomes a receiver of.</param>
    /// <param name="cancel">Ends the wait on the sink.</param>
    /// <exception cref="ScryDisclosureException">
    /// Nobody is named and the settings do not allow that, or the sink did not accept the record. The
    /// question is then not to be answered.
    /// </exception>
    public async ValueTask<ScryDisclosureReview> RecordAsync(
        ScryDisclosureQuestion question,
        string? reviewer,
        ReadOnlyMemory<byte> parameters,
        int results,
        IReadOnlyList<Guid> events,
        Cancel cancel = default)
    {
        var (review, batch) = Make(settings, question, reviewer, parameters, results, events);
        await DisclosureRuntime.AppendAsync(sink, batch, cancel);
        return review;
    }

    // The review and the batch that records it, as every host that records one makes them.
    internal static (ScryDisclosureReview Review, ScryDisclosureBatch Batch) Make(
        ScryDisclosureOptions settings,
        ScryDisclosureQuestion question,
        string? reviewer,
        ReadOnlyMemory<byte> parameters,
        int results,
        IReadOnlyList<Guid> events)
    {
        if (reviewer is null &&
            !settings.AllowAnonymous)
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
            var address = DisclosureRuntime.Address(settings.AddressKey, tagged);
            asked = address;
            contents = [new(address, ScryDisclosureContentKind.Parameters, parameters.Length, parameters)];
        }

        var review = new ScryDisclosureReview(Guid.CreateVersion7(), settings.Clock.GetUtcNow(), question)
        {
            Reviewer = reviewer,
            Parameters = asked,
            Results = results,
            Events = events,
            Node = settings.Node
        };
        var batch = new ScryDisclosureBatch
        {
            EventId = review.Id,
            Review = review,
            Contents = contents
        };
        return (review, batch);
    }
}
