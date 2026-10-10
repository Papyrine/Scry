// What the disclosure explorer's page and its server say to each other. Compiled into both — the
// server package and the browser app — so that neither can come to expect a shape the other does
// not send. Nothing here is part of the wire contract: it is one screen's conversation with the
// host that serves it, and both ends are always the same build.
//
// A question is what the page asks; an answer is what it is told. Every question that shows
// somebody's data is recorded before it is answered, so what a question holds is also what the
// record says was asked.

/// <summary>What the page is told when it loads: what there is to ask about, and what this reviewer may do.</summary>
/// <param name="Sources">The sources the record holds anything of, with the members answers read.</param>
/// <param name="Reviewer">Who the server takes the reader to be, or null where nobody is named.</param>
/// <param name="Export">Whether a result may be written out as a file.</param>
/// <param name="Erase">Whether a row's content may be erased from here.</param>
/// <param name="Verify">Whether the store keeps evidence of its own that can be checked.</param>
sealed record CatalogAnswer(IReadOnlyList<CatalogSource> Sources, string? Reviewer, bool Export, bool Erase, bool Verify);

sealed record CatalogSource(string Name, bool Keyed, IReadOnlyList<CatalogMember> Members);

sealed record CatalogMember(string Name, bool Sensitive);

/// <summary>Where a listing left off, handed back to ask for what follows.</summary>
sealed record PageMark(DateTimeOffset At, Guid Id, int Ordinal);

/// <summary>Who received a row.</summary>
/// <param name="Source">The row's source.</param>
/// <param name="Key">
/// The row's key as it was typed: its values in the key's own order, numbers as they are and text in
/// quotes. The answer says what it was read as.
/// </param>
/// <param name="From">The earliest time to answer for, or null for no bound.</param>
/// <param name="To">The latest.</param>
/// <param name="After">Where an earlier answer left off.</param>
/// <param name="Take">How many to answer with.</param>
sealed record RowQuestion(string Source, string Key, DateTimeOffset? From = null, DateTimeOffset? To = null, PageMark? After = null, int Take = 50);

/// <param name="Key">The key the question was read as: what the record holds a row by.</param>
/// <param name="Rows">Each time the row left, newest first.</param>
/// <param name="Next">Where to ask on from, or null where this was the last of them.</param>
sealed record RowAnswer(string Key, IReadOnlyList<RowLine> Rows, PageMark? Next);

/// <summary>One time a row left the server, or one time a reviewer was since shown the event it left in.</summary>
/// <param name="Event">The event that released it.</param>
/// <param name="Ordinal">Which unit of the event carried it.</param>
/// <param name="Content">That unit's address: the version of the row that was sent.</param>
/// <param name="Via">How the unit reached the row: empty for the root, else a navigation or a join's side.</param>
/// <param name="Members">What the event returned: the members that left in its rows.</param>
/// <param name="Review">Set where this is a reviewer being shown the event, rather than the release itself.</param>
sealed record RowLine(EventLine Event, int Ordinal, string Content, string Via, IReadOnlyList<MemberLine> Members, ReviewLine? Review);

sealed record MemberLine(string Source, string Member, string Use, bool Sensitive);

sealed record ReviewLine(Guid Id, DateTimeOffset At, string? Reviewer, string Question, int Results, int Events);

/// <summary>
/// What a caller received in a range of time. A null caller asks for the answers recorded against
/// nobody; the rest is as for a row.
/// </summary>
sealed record CallerQuestion(string? Caller, DateTimeOffset? From = null, DateTimeOffset? To = null, PageMark? After = null, int Take = 50);

/// <summary>Whether a caller was ever returned a member of a source.</summary>
sealed record MemberQuestion(string? Caller, string Source, string Member, PageMark? After = null, int Take = 50);

sealed record EventsAnswer(IReadOnlyList<EventLine> Events, PageMark? Next);

/// <summary>
/// One event as a listing shows it: who, when, what kind of answer, and how it ended. None of its
/// content.
/// </summary>
/// <remarks>
/// <c>Outcome</c> is how it ended, or null where no close was recorded and it may have been sent.
/// <c>Units</c> is how many units left, where it was closed.
/// </remarks>
sealed record EventLine(
    Guid Id,
    DateTimeOffset At,
    string? Caller,
    string Kind,
    string Source,
    bool Subscribed,
    string Delivery,
    bool Sensitive,
    string? Outcome,
    int? Units,
    string? Node,
    string? Correlation,
    string? ContentType);

/// <summary>One event with everything recorded under it, content included.</summary>
/// <remarks>
/// <c>Request</c> is the request that was answered, as it was recorded. <c>Fields</c> is every member
/// the answer read, with what it did with each. <c>Units</c> is every unit recorded, in order,
/// released or not. <c>Payload</c> is what left, put back together from the store.
/// </remarks>
sealed record EventAnswer(
    EventLine Event,
    string? Request,
    IReadOnlyList<MemberLine> Fields,
    IReadOnlyList<UnitLine> Units,
    string Payload,
    string? Stamp,
    DateTimeOffset? ClosedAt);

/// <summary>One unit of an event.</summary>
/// <remarks>
/// <c>State</c> says whether the store holds its bytes: <c>held</c>, <c>digest</c> for one recorded
/// by digest alone, <c>erased</c>. <c>Released</c> says whether it was handed to the caller, rather
/// than accepted by the store and never sent. <c>Text</c> is what it carried, where the store holds
/// it and it is text. <c>Rows</c> are the rows it was read from.
/// </remarks>
sealed record UnitLine(int Ordinal, string Address, string Kind, int Length, string State, bool Released, string? Text, IReadOnlyList<EntityLine> Rows);

sealed record EntityLine(string Source, string Key, string Via);

/// <summary>How the store stands, and what has lately been done to it and asked of it.</summary>
/// <remarks>
/// <c>Known</c> says whether the store says how it stands at all. One that does not leaves the
/// figures null.
/// </remarks>
sealed record StatusAnswer(
    bool Known,
    long? Pending,
    long? PendingBytes,
    DateTimeOffset? OldestPending,
    long? Events,
    string? ChainHead,
    CheckLine? LastVerification,
    string? Problem,
    IReadOnlyList<ErasureLine> Erasures,
    IReadOnlyList<ReviewLine> Reviews);

sealed record CheckLine(DateTimeOffset At, long Records, bool Intact, long? BrokenAt);

sealed record ErasureLine(DateTimeOffset At, string? By, string Source, string Key, int Units);

sealed record VerifyQuestion(long? From = null, int Count = 10_000);

/// <summary>
/// Erases what was sent of a row. <c>Confirm</c> is the key again, typed a second time: an erasure
/// cannot be taken back.
/// </summary>
sealed record EraseQuestion(string Source, string Key, string Confirm);

/// <summary>
/// Writes a result out as a file: one of the questions, asked again for all of its answer.
/// <c>Format</c> is <c>csv</c> or <c>json</c>, and exactly one of the rest says what of.
/// </summary>
sealed record ExportQuestion(string Format, RowQuestion? Row = null, CallerQuestion? Caller = null, MemberQuestion? Member = null, Guid? Event = null);

/// <summary>Why a question was not answered.</summary>
sealed record Refusal(string Message);
