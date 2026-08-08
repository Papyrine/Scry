/// <summary>
/// The symbols the analyzer matches against, resolved once per compilation rather than per call. The
/// Scry ones are looked up by their full metadata name — the same hardcoded-string approach the
/// generator uses for the annotations, since neither can reference the assembly it is reading.
/// </summary>
sealed class KnownTypes
{
    public INamedTypeSymbol? Queryable { get; }
    public INamedTypeSymbol? Enumerable { get; }
    public INamedTypeSymbol? Extensions { get; }
    public INamedTypeSymbol? Batch { get; }
    public INamedTypeSymbol? Client { get; }
    public INamedTypeSymbol Model { get; }
    public INamedTypeSymbol? EqualityComparer { get; }
    public INamedTypeSymbol? Comparer { get; }

    /// <summary>
    /// The attachment handle type. Null in a compilation that has the model attribute but not the
    /// client assembly, which leaves the attachment rules silent rather than guessing by name.
    /// </summary>
    public INamedTypeSymbol? Attachment { get; }

    KnownTypes(Compilation compilation, INamedTypeSymbol model)
    {
        Model = model;
        Attachment = compilation.GetTypeByMetadataName(SupportedLinq.Attachment);
        Queryable = compilation.GetTypeByMetadataName(SupportedLinq.Queryable);
        Enumerable = compilation.GetTypeByMetadataName(SupportedLinq.Enumerable);
        Extensions = compilation.GetTypeByMetadataName(SupportedLinq.Extensions);
        Batch = compilation.GetTypeByMetadataName(SupportedLinq.Batch);
        Client = compilation.GetTypeByMetadataName(SupportedLinq.Client);
        EqualityComparer = compilation.GetTypeByMetadataName("System.Collections.Generic.IEqualityComparer`1");
        Comparer = compilation.GetTypeByMetadataName("System.Collections.Generic.IComparer`1");
    }

    /// <summary>
    /// Null when nothing in the compilation was generated against a Scry model, which is the common
    /// case for a project that merely has the package restored somewhere in its graph.
    /// </summary>
    public static KnownTypes? For(Compilation compilation)
    {
        var model = compilation.GetTypeByMetadataName(SupportedLinq.ModelAttribute);
        if (model is null)
        {
            return null;
        }

        return new(compilation, model);
    }

    /// <summary>
    /// Whether a type is a generated query model. Read off the attribute rather than the namespace:
    /// a hand-written model that carries it is a Scry source too, and Scry.Generated is a name a
    /// consumer could otherwise occupy.
    /// </summary>
    public bool IsModel(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, Model))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Scry's async terminal standing for a synchronous LINQ one, or null where there is none —
    /// read off the extensions themselves rather than from a list that would have to be kept in step.
    /// </summary>
    public string? AsyncTerminal(string name)
    {
        if (Extensions is null)
        {
            return null;
        }

        var candidate = $"{name}Async";
        foreach (var member in Extensions.GetMembers(candidate))
        {
            if (member is IMethodSymbol)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether a call is one of Scry's own extensions — a terminal, or a batch enrolment.</summary>
    public bool IsScry(INamedTypeSymbol? containing) =>
        SymbolEqualityComparer.Default.Equals(containing, Extensions) ||
        SymbolEqualityComparer.Default.Equals(containing, Batch);

    /// <summary>Whether a type is the attachment handle — a member typed as one is an attachment.</summary>
    public bool IsAttachment(ITypeSymbol? type) =>
        Attachment is not null &&
        SymbolEqualityComparer.Default.Equals(type, Attachment);

    /// <summary>
    /// The members forming a model's row key, as its <c>[ScryModel]</c> names them. Empty for a type
    /// that is not a model, or one carrying no attachment — nothing else declares a key.
    /// </summary>
    public IReadOnlyList<string> KeysOf(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, Model))
                {
                    continue;
                }

                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.Key == "Keys")
                    {
                        return [..argument.Value.Values.Select(_ => _.Value as string).Where(_ => _ is not null)!];
                    }
                }

                return [];
            }
        }

        return [];
    }

    /// <summary>Whether a parameter takes a client-side comparer, which no operator can carry.</summary>
    public bool IsComparer(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        var definition = named.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition, EqualityComparer) ||
               SymbolEqualityComparer.Default.Equals(definition, Comparer);
    }
}
