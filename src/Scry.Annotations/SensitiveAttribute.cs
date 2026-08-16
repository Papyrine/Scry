namespace Scry;

/// <summary>
/// Marks a member — or every member of a type — as one whose values must not travel in a URL or be
/// stored by a cache. The member stays queryable; what changes is how a query touching it is carried
/// and how its answer may be kept.
/// </summary>
/// <remarks>
/// <para>
/// Two rules follow from it, because there are two ways a value escapes. A query comparing this member
/// against a constant is asked with <c>POST</c> rather than as a URL, since a URL is written to the
/// access log of every hop it passes and to the <c>Referer</c> of whatever the page does next. And a
/// response projecting this member is sent <c>no-store</c> with no <c>ETag</c>, since a cacheable
/// response is written to the caller's disk and outlives the session that asked for it.
/// </para>
/// <para>
/// A query that only names the member — ordering by it, or testing it against another column — keeps
/// its URL and its cache: nothing about the value leaves in either direction.
/// </para>
/// <para>
/// The client makes the first choice and the server enforces it, so a client that predates the
/// marking is refused rather than believed. Marking a member therefore moves the
/// <see href="/docs/schema-versioning.md">schema stamp</see>: it changes what a deployed client may
/// do, and moving the stamp is what makes such a client report itself stale and prompt a regenerate.
/// </para>
/// <para>
/// Fields are deliberately not a target. Nothing in Scry reads one — the generator and the server both
/// walk properties — so an attribute here would read as protection while doing nothing at all.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class SensitiveAttribute :
    Attribute;
