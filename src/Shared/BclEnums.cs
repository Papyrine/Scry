// Linked into both Scry.Server and Scry.SourceGenerator, so the two readers agree on which enums
// declared outside the model a member may still be typed as. An enum from the model assembly is
// re-emitted into every client from its metadata; one declared anywhere else cannot be, because the
// generator reads the model DLL alone. An enum of the base class library needs no re-emitting: it is
// already on every client, under the same name and with the same values, so a member typed as one is
// spelled as the BCL type itself on both sides and stays out of the generated enums.
//
// The list is closed. The generator sees only a type reference — a namespace and a name — with no type
// system to ask whether the type is an enum, or which assembly will answer for it, so it can only trust
// a name it was told about. The server, which can ask reflection, holds itself to the same list by type
// identity rather than by name, so a model's own type of the same name is never mistaken for one, and
// refuses the rest at startup rather than describe a member no client could see. Held as types, not
// names, so the home assembly of each is the runtime's business: DayOfWeek is in CoreLib, HttpStatusCode
// is not. Every one is also in netstandard2.0, which the generator targets.
//
// An enum belongs here only if its members never change between runtime versions, or change rarely
// enough that a client and server shipped together will agree: a value is answered by name, and a
// client whose runtime lacks the name cannot read it. HttpStatusCode is the one that has grown (.NET 5
// added codes); see docs/annotations.md.

/// <summary>The enums of the base class library a model member may be typed as.</summary>
static class BclEnums
{
    /// <summary>The enums.</summary>
    static Type[] types =
    [
        typeof(DayOfWeek),
        typeof(DateTimeKind),
        typeof(HttpStatusCode)
    ];

    /// <summary>The enums, by metadata full name.</summary>
    static HashSet<string> fullNames = new(types.Select(_ => _.FullName!));

    /// <summary>
    /// How a listed enum is spelled in generated code — fully qualified, so a model enum of the same
    /// simple name cannot capture it — or null for a type that is not listed. By name, for the
    /// generator, which has only a type reference to go on.
    /// </summary>
    public static string? Display(string? fullName)
    {
        if (fullName is not null &&
            fullNames.Contains(fullName))
        {
            return "global::" + fullName;
        }

        return null;
    }

    /// <summary>
    /// The same spelling, or null, by type identity: a listed enum only, never another type that
    /// happens to share one's name.
    /// </summary>
    public static string? Display(Type type)
    {
        if (types.Contains(type))
        {
            return "global::" + type.FullName;
        }

        return null;
    }
}
