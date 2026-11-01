/// <summary>
/// A per-type cache that stops growing at <paramref name="capacity"/>: past it, a value is made and
/// handed back without being kept. For caches keyed by types a request can choose — a deduplicated
/// projection closes a row type over whatever member types it names — where an unbounded dictionary
/// would grow with every new closing a client thought of.
/// </summary>
sealed class BoundedTypeCache<TValue>(int capacity)
{
    ConcurrentDictionary<Type, TValue> values = new();
    int count;

    public TValue GetOrAdd(Type type, Func<Type, TValue> make)
    {
        if (values.TryGetValue(type, out var value))
        {
            return value;
        }

        value = make(type);
        // Counted on the side: ConcurrentDictionary.Count takes every lock in the dictionary.
        if (Volatile.Read(ref count) < capacity &&
            values.TryAdd(type, value))
        {
            Interlocked.Increment(ref count);
        }

        return value;
    }
}
