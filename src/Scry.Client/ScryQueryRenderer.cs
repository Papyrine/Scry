namespace Scry;

/// <summary>Why <see cref="ScryQueryRenderer.TryRender(QueryRequest, out string?, out RenderRefusal)"/> declined to render a request.</summary>
public enum RenderRefusal
{
    None,

    /// <summary>
    /// The request compares a <c>[Sensitive]</c> member against a constant. The constant is the
    /// secret, and a rendered snippet is made to be shared — so it is never produced.
    /// </summary>
    SensitiveConstants,

    /// <summary>The terminal has no explorer spelling that produces the same wire bytes.</summary>
    UnsupportedTerminal,

    /// <summary>An operator or expression shape the snippet dialect cannot faithfully re-spell.</summary>
    UnsupportedShape,

    /// <summary>
    /// The request needs a registered query model — an enum constant's type, a nullable member's
    /// <c>.Value</c>, an <c>OfType</c> target — and no model for the source is known here.
    /// </summary>
    UnresolvedModel
}

/// <summary>
/// Renders a wire <see cref="QueryRequest"/> back into the C# LINQ snippet dialect the query
/// explorer accepts — the inverse of the capture the client performs. A rendered snippet
/// round-trips: translating it through <c>ToScryRequest</c> produces the original request,
/// byte for byte.
/// </summary>
public static class ScryQueryRenderer
{
    /// <summary>Renders the request, or returns false where no faithful snippet exists.</summary>
    public static bool TryRender(QueryRequest request, [NotNullWhen(true)] out string? code) =>
        TryRender(request, out code, out _);

    /// <summary>Renders the request, also reporting why rendering was refused.</summary>
    public static bool TryRender(QueryRequest request, [NotNullWhen(true)] out string? code, out RenderRefusal refusal) =>
        Render(request, SensitiveModel.ModelFor(request.Root), out code, out refusal);

    /// <summary>
    /// Renders against an explicitly supplied root model, for callers outside the ambient source
    /// registry — a sidecar reading requests this process never captured.
    /// </summary>
    public static bool TryRender(QueryRequest request, Type rootModel, [NotNullWhen(true)] out string? code, out RenderRefusal refusal) =>
        Render(request, rootModel, out code, out refusal);

    static bool Render(QueryRequest request, Type? rootModel, out string? code, out RenderRefusal refusal)
    {
        code = null;

        // The gate comes first: a request that has to travel in a body carries a secret in a
        // constant, and a snippet — minted into a shareable link — must not. A sensitive member
        // that is only ordered by or projected does not trip this, exactly as the transport rule.
        if (ScryClient.RequiresBody(request))
        {
            refusal = RenderRefusal.SensitiveConstants;
            return false;
        }

        try
        {
            code = new QueryRenderer(rootModel).Render(request);
            refusal = RenderRefusal.None;
            return true;
        }
        catch (RenderRefusalException refused)
        {
            refusal = refused.Refusal;
            return false;
        }
    }
}
