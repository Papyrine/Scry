using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Scry;

/// <summary>
/// Reports what a <see cref="DbContext"/> saved to <see cref="ScryChanges"/>, so the live queries
/// reading those entities are asked again. Registered as a singleton by <c>AddScry</c>; a host adds it
/// to the contexts that write:
/// <code>
/// services.AddDbContext&lt;AppContext&gt;((provider, builder) =&gt; builder
///     .UseSqlServer(connection)
///     .AddInterceptors(provider.GetRequiredService&lt;ScryChangeInterceptor&gt;()));
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// A change is reported once it can be read, never before: after the save where no transaction is
/// open, and after the commit where one is — a save inside a transaction that then rolls back reports
/// nothing. A live query asked again before the commit would read what was there before it, find its
/// answer unchanged, and have no reason to look again.
/// </para>
/// <para>
/// What it cannot see is what never passes through <c>SaveChanges</c>: <c>ExecuteUpdate</c> and
/// <c>ExecuteDelete</c>, raw SQL, a trigger, another process. Those are
/// <see cref="ScryChanges.Notify{TEntity}"/>'s, or a change probe's. Nor can it see a transaction
/// handed to the context with <c>UseTransaction</c> and committed by its owner — nothing tells the
/// context that happened — which a live query's own poll is what catches.
/// </para>
/// </remarks>
public sealed class ScryChangeInterceptor(ScryChanges changes) :
    SaveChangesInterceptor
{
    // What the save in progress on each context is about to write. Held from before the save, when the
    // entries still say what they are, to after it, when it is known to have happened.
    ConditionalWeakTable<DbContext, HashSet<string>> saving = new();

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        Cancel cancellationToken = default)
    {
        Collect(eventData.Context);
        return new(result);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        Cancel cancellationToken = default)
    {
        Saved(eventData.Context);
        return new(result);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Discard(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, Cancel cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SaveChangesCanceled(DbContextEventData eventData) =>
        Discard(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, Cancel cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        changes.Attach(context.Model);

        // Entries() runs change detection, which the save itself has not yet done at this point: a
        // property set on a snapshot-tracked entity is still Unchanged until something looks.
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            names.Add(EntityNames.Root(entry.Metadata));
            if (entry.State == EntityState.Deleted)
            {
                EntityNames.AddCascades(entry.Metadata, names);
            }
        }

        saving.AddOrUpdate(context, names);
    }

    void Saved(DbContext? context)
    {
        if (context is null ||
            !saving.TryGetValue(context, out var names))
        {
            return;
        }

        saving.Remove(context);
        if (names.Count == 0)
        {
            return;
        }

        if (context.Database.CurrentTransaction is not null)
        {
            TransactionWatch.Hold(context, changes, names);
            return;
        }

        if (System.Transactions.Transaction.Current is { } ambient)
        {
            TransactionWatch.Hold(ambient, changes, names);
            return;
        }

        changes.Raise([.. names]);
    }

    void Discard(DbContext? context)
    {
        if (context is not null)
        {
            saving.Remove(context);
        }
    }
}
