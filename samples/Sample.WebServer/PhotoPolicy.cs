/// <summary>
/// The check that authorizes <see cref="Employee.Photo"/>, and the reason the sample's home page can
/// draw a face at all: an attachment exposed without one is a startup failure.
/// </summary>
/// <remarks>
/// The sample has no sign-in, so every row is authorized — see <see cref="HandbookPolicy"/> for what
/// a real one reads instead.
/// </remarks>
public sealed class PhotoPolicy :
    IAttachmentPolicy<Employee>
{
    public bool Authorize(ScryAttachmentContext context) => true;
}
