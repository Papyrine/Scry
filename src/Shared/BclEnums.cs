// Linked into both Scry.Server and Scry.SourceGenerator, so the two readers agree on which enums
// declared outside the model a member may still be typed as. An enum from the model assembly is
// re-emitted into every client from its metadata; one declared anywhere else cannot be, because the
// generator reads the model DLL alone. An enum of the base class library needs no re-emitting: it is
// already on every client, under the same name and with the same values, so a member typed as one is
// spelled as the BCL type itself on both sides and stays out of the generated enums.
//
// The list is closed and matched by full name. The generator sees only a type reference — a
// namespace and a name — with no type system to ask whether the type is an enum, or which assembly
// will answer for it, so it can only trust a name it was told about. The server, which could ask
// reflection, holds itself to the same list and refuses the rest at startup, rather than describe a
// member no client could see.

/// <summary>The enums of the base class library a model member may be typed as.</summary>
static class BclEnums
{
    /// <summary>The enums, by metadata full name.</summary>
    static HashSet<string> fullNames =
    [
        "System.DayOfWeek",
        "System.DateTimeKind"
    ];

    /// <summary>
    /// How a listed enum is spelled in generated code — fully qualified, so a model enum of the same
    /// simple name cannot capture it — or null for a type that is not listed.
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
}
