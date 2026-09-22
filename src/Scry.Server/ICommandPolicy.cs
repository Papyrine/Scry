// ReSharper disable UnusedTypeParameter
namespace Scry;

/// <summary>
/// Decides whether a caller may send a command at all. Asked on every command before anything is read,
/// and on every query projecting the command's capability, so a caller it refuses has every
/// <c>Can{Command}</c> read as false. Register with <c>[Command(Policy = typeof(...))]</c> or
/// <see cref="ScryOptions.AddCommandPolicy{TCommand, TPolicy}"/>.
/// </summary>
/// <remarks>
/// Resolved from the request's services where it was registered and constructed otherwise, as a row
/// policy is. Identity comes from the authenticated principal reached through
/// <see cref="ScryPolicyContext.Services"/>, never from a header, which the client chooses.
/// </remarks>
// begin-snippet: commandPolicyInterface
public interface ICommandPolicy<TCommand>
{
    bool Allow(ScryPolicyContext context);
}

/// <summary>
/// Decides, for a targeted command, which rows of <typeparamref name="TEntity"/> a caller may send it
/// against — as an expression, so it runs in the database: as the check a command's row has to pass,
/// and as the <c>Can{Command}</c> member a query reads per row.
/// </summary>
/// <remarks>
/// Composed with the target's own row policies rather than instead of them: a row the caller cannot
/// read is not one they can send a command against, whatever this answers.
/// </remarks>
public interface ICommandPolicy<TCommand, TEntity> :
    ICommandPolicy<TCommand>
{
    Expression<Func<TEntity, bool>> Rows(ScryPolicyContext context);
}
// end-snippet
