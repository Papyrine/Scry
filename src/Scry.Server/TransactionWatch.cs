using Microsoft.EntityFrameworkCore.Diagnostics;
using AmbientTransaction = System.Transactions.Transaction;

/// <summary>
/// Holds what was saved inside a transaction until the transaction says how it ended, and reports it
/// only if that was a commit. Knows, too, which of a context's transactions were handed to it rather
/// than begun by it: those never say how they ended, and what is saved in one is not held at all.
/// </summary>
/// <remarks>
/// <para>
/// EF says a transaction ended through <c>IDbTransactionInterceptor</c>, which belongs to the
/// relational package this assembly does not reference. It says the same thing on its diagnostic
/// listener, by name, to anyone — and the payload's base type, which carries the context, is EF Core
/// proper. So the events are listened for by name: one process-wide subscription, enabled for four
/// event names and nothing else, made when the first <see cref="ScryChangeInterceptor"/> is built and
/// kept for the life of the process. Made then rather than at the first save held, because a
/// transaction handed to a context is announced before anything is saved in it.
/// </para>
/// <para>
/// What a payload carries beyond the context — the transaction's id, which action failed — is the
/// relational package's too, and is read by name through reflection, once per payload type.
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
    const string used = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionUsed";

    static ConditionalWeakTable<DbContext, Held> contexts = new();
    static ConditionalWeakTable<AmbientTransaction, Held> ambients = new();

    // The ids of the transactions each context was handed with UseTransaction. Their commit is the
    // owner's, and nothing tells the context it happened.
    static ConditionalWeakTable<DbContext, HashSet<Guid>> handed = new();
    static int subscribed;

    /// <summary>
    /// Starts listening for EF's transaction events, once per process. Idempotent, and called before
    /// any context carrying an interceptor can be handed a transaction.
    /// </summary>
    public static void Listen()
    {
        if (Interlocked.Exchange(ref subscribed, 1) == 0)
        {
            DiagnosticListener.AllListeners.Subscribe(new Listeners());
        }
    }

    /// <summary>Holds a save made inside the context's own transaction.</summary>
    public static void Hold(DbContext context, ScryChanges changes, HashSet<string> names)
    {
        Listen();
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

    /// <summary>
    /// Whether the context was handed the transaction rather than beginning it, in which case no
    /// commit will be heard of and a save inside it has to be reported at the save.
    /// </summary>
    public static bool IsExternal(DbContext context, Guid transactionId)
    {
        if (!handed.TryGetValue(context, out var ids))
        {
            return false;
        }

        lock (ids)
        {
            return ids.Contains(transactionId);
        }
    }

    static void Received(string name, object? payload)
    {
        if (payload is not DbContextEventData {Context: { } context})
        {
            return;
        }

        if (name == used)
        {
            Used(context, payload);
            return;
        }

        Ended(name, context, payload);
    }

    static void Used(DbContext context, object payload)
    {
        if (Property(payload, "TransactionId") is not Guid id)
        {
            return;
        }

        var ids = handed.GetValue(context, _ => []);
        lock (ids)
        {
            ids.Add(id);
        }
    }

    static void Ended(string name, DbContext context, object payload)
    {
        // The same event for a savepoint that failed as for a commit that did, told apart by the action.
        // A savepoint failing ends nothing: the transaction is still open, and the app may still commit
        // it, so what is held stays held.
        if (name == failed &&
            Property(payload, "Action") is not ("Commit" or "Rollback"))
        {
            return;
        }

        if (!contexts.TryGetValue(context, out var held))
        {
            return;
        }

        contexts.Remove(context);
        if (name == committed)
        {
            held.Flush();
        }
    }

    // The payloads are the relational package's, so what they carry beyond the context is read by name.
    // Every event of a kind carries the same payload type, so the lookup is made once per type and name.
    static ConcurrentDictionary<(Type Payload, string Name), PropertyInfo?> properties = new();

    static object? Property(object payload, string name) =>
        properties
            .GetOrAdd((payload.GetType(), name), static key => key.Payload.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance))
            ?.GetValue(payload);

    sealed class Held(ScryChanges changes)
    {
        HashSet<string> names = [with(StringComparer.Ordinal)];

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
                value.Subscribe(new Events(), _ => _ is committed or rolledBack or failed or used);
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
            Received(value.Key, value.Value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
