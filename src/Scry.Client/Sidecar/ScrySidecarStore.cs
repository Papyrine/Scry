namespace Scry;

/// <summary>
/// Holds the exchanges the sidecar has captured. A singleton, deliberately apart from
/// <see cref="ScrySidecarHandler"/>: the HTTP factory rotates handlers, and a log that rotated with
/// them would forget everything every couple of minutes.
/// </summary>
public sealed class ScrySidecarStore(ScrySidecarOptions options)
{
    readonly object sync = new();
    readonly List<ScrySidecarEntry> entries = [];
    int nextId;

    /// <summary>Raised after an entry is added or the log is cleared.</summary>
    public event Action? Changed;

    /// <summary>A snapshot of the captured entries, oldest first.</summary>
    public IReadOnlyList<ScrySidecarEntry> Entries
    {
        get
        {
            lock (sync)
            {
                return [.. entries];
            }
        }
    }

    internal ScrySidecarEntry Add(ScrySidecarEntry entry)
    {
        lock (sync)
        {
            entry = entry with {Id = ++nextId};
            entries.Add(entry);
            while (entries.Count > options.MaxEntries)
            {
                entries.RemoveAt(0);
            }
        }

        Changed?.Invoke();
        return entry;
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
        }

        Changed?.Invoke();
    }
}
