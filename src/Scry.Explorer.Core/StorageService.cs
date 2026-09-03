namespace Scry;

/// <summary>
/// Where <see cref="StorageService"/> keeps its values. In the browser that is localStorage through
/// the host module; tests use <see cref="InMemoryStorageBackend"/>.
/// </summary>
public interface IStorageBackend
{
    string? Get(string key);

    /// <summary>False when the value could not be stored — a full quota, or storage disabled.</summary>
    bool Set(string key, string value);

    void Remove(string key);

    IReadOnlyList<string> Keys();
}

/// <summary>A dictionary-backed backend for tests and non-browser hosts.</summary>
public sealed class InMemoryStorageBackend :
    IStorageBackend
{
    readonly Dictionary<string, string> values = [];

    public string? Get(string key) =>
        values.GetValueOrDefault(key);

    public bool Set(string key, string value)
    {
        values[key] = value;
        return true;
    }

    public void Remove(string key) =>
        values.Remove(key);

    public IReadOnlyList<string> Keys() =>
        [.. values.Keys];
}

/// <summary>
/// Namespaced persistent storage: every key is stored as <c>{ns}:{key}</c>, corrupt values self-heal,
/// setting empty removes, and <see cref="Clear"/> only touches this namespace.
/// </summary>
/// <remarks>
/// The explorer is a page of its own rather than a component in a host app, so the namespace is a
/// constant rather than a parameter — it exists to keep <see cref="Clear"/> honest, not to isolate two
/// instances from each other.
/// </remarks>
public sealed class StorageService(IStorageBackend backend, string ns = "scry")
{
    string Prefix => $"{ns}:";

    string FullKey(string key) =>
        Prefix + key;

    public string? Get(string key)
    {
        var value = backend.Get(FullKey(key));
        if (value is null)
        {
            return null;
        }

        // A literal "null"/"undefined" is a serialization accident from a previous session; treat it
        // as corrupt and heal the slot.
        if (value is "null" or "undefined")
        {
            backend.Remove(FullKey(key));
            return null;
        }

        return value;
    }

    /// <summary>
    /// Stores the value. An empty value removes the key. False means the quota was exceeded or storage
    /// refused the write — which a debug convenience reports rather than throws.
    /// </summary>
    public bool Set(string key, string value)
    {
        if (value.Length == 0)
        {
            backend.Remove(FullKey(key));
            return true;
        }

        return backend.Set(FullKey(key), value);
    }

    public void Remove(string key) =>
        backend.Remove(FullKey(key));

    /// <summary>
    /// Reads a key stored outside the namespace. Two of them exist: the theme, which an inline script
    /// in index.html reads before first paint and so cannot be renamed, and the history key used
    /// before entries carried labels.
    /// </summary>
    public string? RawGet(string key) =>
        backend.Get(key);

    /// <summary>Writes a key stored outside the namespace. See <see cref="RawGet"/>.</summary>
    public bool RawSet(string key, string value) =>
        backend.Set(key, value);

    /// <summary>Removes a key stored outside the namespace. See <see cref="RawGet"/>.</summary>
    public void RawRemove(string key) =>
        backend.Remove(key);

    /// <summary>Removes every key in this namespace, and nothing outside it.</summary>
    public void Clear()
    {
        foreach (var key in backend.Keys().Where(_ => _.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            backend.Remove(key);
        }
    }
}
