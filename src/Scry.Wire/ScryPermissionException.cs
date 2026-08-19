namespace Scry;

/// <summary>
/// Thrown where a row policy denies a row the query read and that policy is configured to fail the
/// request rather than hide it (<c>DeniedRowMode.Error</c>). Server-side it becomes an HTTP 403;
/// client-side it is what a 403 surfaces as, so both sides of a call name the same type.
/// </summary>
/// <remarks>
/// The message is fixed and says nothing about which source, member, row, or policy denied the query:
/// the mode already discloses that something matched, and naming what would disclose the shape of the
/// policy on top of it.
/// </remarks>
public sealed class ScryPermissionException(string message) :
    Exception(message)
{
    /// <summary>The only message this exception ever carries, on either side of the wire.</summary>
    public const string DeniedMessage = "The query was denied by a server policy.";
}
