/// <summary>
/// What an entity is called when one node tells another it changed, and when a live query says what it
/// read. Both sides have to arrive at the same name from different ends — a saved entry on one, an
/// expression on the other — so both come through here.
/// </summary>
/// <remarks>
/// The name is the root of the hierarchy, reached through any ownership first. A derived type's rows
/// live in its root's table under table-per-hierarchy and are read through the root under every
/// mapping, and an owned type's rows are only ever read through their owner. A name rather than a CLR
/// type because a shared-type entity — a many-to-many join table above all — has no CLR type of its
/// own to be told apart by, and because a name is already something a backplane can carry.
/// </remarks>
static class EntityNames
{
    public static string Root(IReadOnlyEntityType type)
    {
        var current = type;
        while (current.FindOwnership() is { } ownership)
        {
            current = ownership.PrincipalEntityType;
        }

        return current.GetRootType().Name;
    }

    /// <summary>
    /// The names a CLR type is written under. More than one where the type is mapped more than once,
    /// and the type's own name where the model does not map it at all — a POCO source, which nothing
    /// but its host can report a change to.
    /// </summary>
    public static IReadOnlyList<string> For(IModel model, Type type)
    {
        var names = model
            .FindEntityTypes(type)
            .Select(Root)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            names.Add(type.FullName ?? type.Name);
        }

        return names;
    }

    /// <summary>
    /// Adds what deleting a row of <paramref name="type"/> changes besides itself: the types whose rows
    /// the database deletes or nulls in answer. Those rows were never loaded, so the change tracker
    /// holds no entry for them and nothing else would say they changed.
    /// </summary>
    public static void AddCascades(IEntityType type, ISet<string> names)
    {
        HashSet<IEntityType> visited = [type];
        AddCascades(type, names, visited);
    }

    static void AddCascades(IEntityType type, ISet<string> names, HashSet<IEntityType> visited)
    {
        foreach (var key in type.GetReferencingForeignKeys())
        {
            // The client-side behaviours act only on entries the tracker already holds, and those
            // report themselves.
            if (key.DeleteBehavior is not (DeleteBehavior.Cascade or DeleteBehavior.SetNull))
            {
                continue;
            }

            var dependent = key.DeclaringEntityType;
            names.Add(Root(dependent));

            // A row the cascade deleted cascades in its turn; one it only nulled does not.
            if (key.DeleteBehavior == DeleteBehavior.Cascade &&
                visited.Add(dependent))
            {
                AddCascades(dependent, names, visited);
            }
        }
    }
}
