// Linked into both Scry.Server and Scry.SourceGenerator, so the two readers agree on which collection
// declarations expose a [QueryableCollection] member. The generator reads metadata and has no type
// system to ask about assignability, so the shapes are a closed list of generic definitions matched
// by name; the server, which could accept anything enumerable, holds itself to the same list and
// refuses the rest at startup rather than expose a member no client could see.

/// <summary>The collection declarations a <c>[QueryableCollection]</c> member may have, besides a one-dimensional array.</summary>
static class CollectionShapes
{
    /// <summary>The generic type definitions, by metadata full name.</summary>
    public static readonly HashSet<string> GenericDefinitions =
    [
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.Collections.ObjectModel.ObservableCollection`1"
    ];

    /// <summary>The same list as a reader would write it, for a message naming what is accepted.</summary>
    public const string Described = "an array, List<T>, HashSet<T>, Collection<T>, ObservableCollection<T>, or one of the ICollection<T>, IEnumerable<T>, IList<T>, IReadOnlyCollection<T>, IReadOnlyList<T>, ISet<T> interfaces";
}
