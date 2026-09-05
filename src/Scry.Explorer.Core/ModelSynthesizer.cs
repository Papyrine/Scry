namespace Scry;

/// <summary>
/// Turns a <see cref="ScryIntrospection"/> into the C# source the design-time generator would emit:
/// the re-emitted enums, one query-model class per type, and a <c>ScryQuery</c> facade exposing each
/// source as <see cref="IQueryable{T}"/>. Compiled in-browser so Roslyn can complete against it.
/// </summary>
public static class ModelSynthesizer
{
    /// <param name="introspection">The allow-listed surface the server published.</param>
    /// <param name="executable">
    /// When true, emit a real client-backed <c>ScryQuery</c> (for compiling + running a query); when
    /// false, emit a shape-only facade sufficient for completion (no Scry.Client dependency).
    /// </param>
    public static string Synthesize(ScryIntrospection introspection, bool executable = false)
    {
        var builder = new StringBuilder();
        // Mirrors the generator's header: a deprecated model type is referenced by every navigation to
        // it and by its own entry point, and those uses are synthesized code the snippet author cannot
        // edit. Their own uses, in the snippet itself, still warn.
        builder.AppendLine(
            """
            #nullable enable
            #pragma warning disable CS0612, CS0618
            namespace Scry.Generated;
            """);
        builder.AppendLine();

        foreach (var enumeration in introspection.Enums)
        {
            // Mirrors ScryGenerator.EmitEnums: the values, the underlying type and [Flags] decide
            // what a member means, so a snippet's constants have to resolve as generated code's do.
            if (enumeration.IsFlags)
            {
                builder.AppendLine("[global::System.Flags]");
            }

            var underlying = enumeration.Underlying == "int" ? "" : $" : {enumeration.Underlying}";
            builder.AppendLine($"public enum {enumeration.Name}{underlying}");
            builder.AppendLine("{");
            for (var i = 0; i < enumeration.Values.Count; i++)
            {
                var value = enumeration.Constants is { } constants ? $" = {constants[i]}" : "";
                builder.AppendLine($"    {CSharpIdentifier.Escape(enumeration.Values[i])}{value},");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        foreach (var type in introspection.Types)
        {
            // Mirrors the generator: not sealed, and inheriting where the server's types do, so a
            // snippet can narrow with OfType exactly as generated client code can.
            var inherits = type.Base is null ? "" : $" : {type.Base}";
            var attribute = executable && introspection.Sources.Any(_ => _.Model == type.Model)
                ? $"[global::Scry.ScryModel({Arguments(type, introspection)})]{Environment.NewLine}"
                : "";

            builder.AppendLine(
                $$"""
                {{Obsolete(type.Obsolete)}}{{Sensitive(type.IsSensitive, executable)}}{{attribute}}public class {{type.Model}}{{inherits}}
                {
                """);
            foreach (var member in type.Members)
            {
                var initializer = member.NeedsNullDefault ? " = null!;" : "";
                builder.Append(Obsolete(member.Obsolete, indent: "    "));
                builder.Append(Sensitive(member.IsSensitive, executable, indent: "    "));
                builder.AppendLine($"    public {member.TypeDisplay} {CSharpIdentifier.Escape(member.Name)} {{ get; init; }}{initializer}");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        builder.AppendLine(
            """
            public sealed class ScryQuery
            {
            """);
        if (executable)
        {
            // Mirrors the generator: each source is backed by the real client so the captured query
            // can be translated via ToScryRequest, and carries the same schema stamp — here taken
            // from the introspection the explorer just fetched.
            builder.AppendLine(
                $$"""
                    public const string SchemaStamp = "{{introspection.SchemaStamp}}";

                    readonly global::Scry.ScryClient client;

                    public ScryQuery(global::Scry.ScryClient client)
                    {
                        this.client = client;
                        client.SchemaStamp = SchemaStamp;
                    }
                """);
            foreach (var source in introspection.Sources)
            {
                // Mirrors the generator's entry point, scalar member list included, so a snippet without
                // a Select produces the same wire request a generated client would.
                var members = string.Join(
                    ", ",
                    ScalarMembers(introspection.Types.Single(_ => _.Model == source.Model), introspection)
                        .Select(Literal));
                builder.Append(Obsolete(source.Obsolete, indent: "    "));
                builder.AppendLine($"    public global::System.Linq.IQueryable<{source.Model}> {source.Name} => client.Source<{source.Model}>({Literal(source.Name)}, [{members}]);");
            }
        }
        else
        {
            // Completion only needs the shape — no Scry.Client dependency.
            foreach (var source in introspection.Sources)
            {
                builder.Append(Obsolete(source.Obsolete, indent: "    "));
                builder.AppendLine($"    public global::System.Linq.IQueryable<{source.Model}> {source.Name} => null!;");
            }
        }

        builder.AppendLine("}");

        return builder.ToString();
    }

    // The scalar members a snippet projects when it writes no Select: declared plus inherited,
    // base-first, matching the generator's own walk so the two produce the same wire request.
    static List<string> ScalarMembers(ScryTypeInfo type, ScryIntrospection introspection)
    {
        var members = new List<string>();
        if (type.Base is { } baseModel &&
            introspection.Types.FirstOrDefault(_ => _.Model == baseModel) is { } baseType)
        {
            members.AddRange(ScalarMembers(baseType, introspection));
        }

        // Attachments are excluded with navigations and collections, matching the generator: no query
        // reads one, so it is not a member a projection can default to.
        members.AddRange(
            type.Members
                .Where(_ => _ is {IsNavigation: false, IsCollection: false, IsAttachment: false})
                .Select(_ => _.Name));
        return members;
    }

    // Mirrors ScryGenerator.Arguments: the source and its scalar members, plus the key and attachment
    // names for a type carrying one. A type without an attachment emits exactly what it always did.
    static string Arguments(ScryTypeInfo type, ScryIntrospection introspection)
    {
        var source = introspection.Sources.First(_ => _.Model == type.Model).Name;
        var written = string.Join(", ", new[] {source}.Concat(ScalarMembers(type, introspection)).Select(Literal));

        var attachments = Attachments(type, introspection);
        if (attachments.Count == 0)
        {
            return written;
        }

        return $"{written}, Keys = new[] {{{string.Join(", ", (type.Keys ?? []).Select(Literal))}}}, Attachments = new[] {{{string.Join(", ", attachments.Select(Literal))}}}";
    }

    // Declared plus inherited, like ScalarMembers — an attachment declared on a base is the derived
    // row's too.
    static List<string> Attachments(ScryTypeInfo type, ScryIntrospection introspection)
    {
        var members = new List<string>();
        if (type.Base is { } baseModel &&
            introspection.Types.FirstOrDefault(_ => _.Model == baseModel) is { } baseType)
        {
            members.AddRange(Attachments(baseType, introspection));
        }

        members.AddRange(
            type.Members
                .Where(_ => _.IsAttachment)
                .Select(_ => _.Name));
        return members;
    }

    /// <summary>
    /// Mirrors the generator's <c>[ScrySensitive]</c> emission, so a snippet written in the explorer
    /// makes the same transport choice compiled client code would: the explorer sends what
    /// <c>ToScryRequest</c> produced, and that choice is read off these attributes.
    /// </summary>
    /// <remarks>
    /// Only where the models are executable. The completion-only facade names no Scry.Client type at
    /// all — it exists to give the editor a shape, and it never sends anything.
    /// </remarks>
    static string Sensitive(bool sensitive, bool executable, string indent = "")
    {
        if (sensitive && executable)
        {
            return $"{indent}[global::Scry.ScrySensitive]{Environment.NewLine}";
        }

        return "";
    }

    /// <summary>
    /// Mirrors the generator's <c>[Obsolete]</c> emission, so a snippet written in the explorer warns
    /// on a deprecated source or member exactly where compiled client code would. Null means the model
    /// did not deprecate it; empty means it did, with nothing to add.
    /// </summary>
    static string Obsolete(string? message, string indent = "")
    {
        if (message is null)
        {
            return "";
        }

        var arguments = message.Length == 0 ? "" : $"({Literal(message)})";
        return $"{indent}[global::System.ObsoleteAttribute{arguments}]{Environment.NewLine}";
    }

    // Introspection is fetched over HTTP and compiled in-browser, so every string it contributes is
    // formatted into a literal rather than interpolated between bare quotes.
    static string Literal(string value) =>
        SymbolDisplay.FormatLiteral(value, quote: true);
}
