/// <summary>
/// The check that authorizes <see cref="Department.Handbook"/>. A source exposing an attachment with
/// none refuses to start: the fetch is reached by row key, so unlike the query endpoint — where a
/// default-deny allow-list already stands in the way — there is nothing else here to say no.
/// </summary>
/// <remarks>
/// The sample has no sign-in, so every row is authorized. A real one resolves the principal from
/// <see cref="ScryAttachmentContext.Services"/> and answers per row, reading
/// <see cref="ScryAttachmentContext.KeyValues"/> for which row is being asked about — never a request
/// header, which is the caller's own to write.
/// </remarks>
public sealed class HandbookPolicy :
    IAttachmentPolicy<Department>
{
    public bool Authorize(ScryAttachmentContext context) => true;
}
