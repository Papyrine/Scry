using Microsoft.EntityFrameworkCore.Diagnostics;
using AmbientTransaction = System.Transactions.Transaction;

/// <summary>
/// Holds what was saved inside a transaction until the transaction says how it ended, and reports it
/// only if that was a commit.
/// </summary>
/// <remarks>
/// <para>
/// EF says a transaction ended through <c>IDbTransactionInterceptor</c>, which belongs to the
/// relational package this assembly does not reference. It says the same thing on its diagnostic
/// listener, by name, to anyone — and the payload's base type, which carries the context, is EF Core
/// proper. So the events are listened for by name: one process-wide subscription, enabled for three
/// event names and nothing else, made the first time a save lands inside a transaction and kept for
/// the life of the process.
/// </para>
/// <para>
/// A context abandoned mid-transaction is never heard from again, and what it saved is dropped with
/// it, unreported — which is right, since the database rolled it back.
/// </para>
/// </remarks>
static class TransactionWatch
{
    const string listenerName = "Microsoft.EntityFrameworkCore";
    const string committed = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted";
    const string rolledBack = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionRolledBack";
    const string failed = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionError";

    static ConditionalWeakTable<DbContext, Held> contexts = new();
    static ConditionalWeakTable<AmbientTransaction, Held> ambients = new();
    static int subscribed;

    /// <summary>Holds a save made inside the context's own transaction.</summary>
    public static void Hold(DbContext context, ScryChanges changes, HashSet<string> names)
    {
        if (Interlocked.Exchange(ref subscribed, 1) == 0)
        {
            DiagnosticListener.AllListeners.Subscribe(new Listeners());
        }

        contexts
            .GetValue(context, _ => new(changes))
            .Add(names);
    }

    /// <summary>Holds a save made inside an ambient <c>TransactionScope</c>.</summary>
    public static void Hold(AmbientTransaction ambient, ScryChanges changes, HashSet<string> names)
    {
        if (!ambients.TryGetValue(ambient, out var held))
        {
            held = ambients.GetValue(ambient, _ => new(changes));
            ambient.TransactionCompleted += (_, completed) =>
            {
                if (completed.Transaction?.TransactionInformation.Status == System.Transactions.TransactionStatus.Committed)
                {
                    held.Flush();
                }
            };
        }

        held.Add(names);
    }

    static void Ended(string name, object? payload)
    {
        if (payload is not DbContextEventData {Context: { } context} ||
            !contexts.TryGetValue(context, out var held))
        {
            return;
        }

        contexts.Remove(context);
        if (name == committed)
        {
            held.Flush();
        }
    }

    sealed class Held(ScryChanges changes)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        public void Add(HashSet<string> saved)
        {
            lock (names)
            {
                names.UnionWith(saved);
            }
        }

        public void Flush()
        {
            string[] flushed;
            lock (names)
            {
                flushed = [.. names];
                names.Clear();
            }

            if (flushed.Length > 0)
            {
                changes.Raise(flushed);
            }
        }
    }

    // EF makes one listener per internal service provider, all under the same name, so this is told of
    // each as it appears — those that already exist included — and subscribes to every one.
    sealed class Listeners :
        IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == listenerName)
            {
                value.Subscribe(new Events(), _ => _ is committed or rolledBack or failed);
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    sealed class Events :
        IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) =>
            Ended(value.Key, value.Value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
