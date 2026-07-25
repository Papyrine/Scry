namespace Scry.Explorer.Core;

/// <summary>
/// Turns a <see cref="ScryIntrospection"/> into the C# source the design-time generator would emit:
/// the re-emitted enums, one query-model class per type, and a <c>ScryQuery</c> facade exposing each
/// source as <see cref="IQueryable{T}"/>. Compiled in-browser so Roslyn can complete against it.
/// </summary>
public static class ModelSynthesizer
{
    /// <param name="executable">
    /// When true, emit a real client-backed <c>ScryQuery</c> (for compiling + running a query); when
    /// false, emit a shape-only facade sufficient for completion (no Scry.Client dependency).
    /// </param>
    public static string Synthesize(ScryIntrospection introspection, bool executable = false)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            """
            #nullable enable
            namespace Scry.Generated;
            """);
        builder.AppendLine();

        foreach (var enumeration in introspection.Enums)
        {
            builder.AppendLine(
                $$"""
                public enum {{enumeration.Name}}
                {
                """);
            foreach (var value in enumeration.Values)
            {
                builder.AppendLine($"    {value},");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        foreach (var type in introspection.Types)
        {
            builder.AppendLine(
                $$"""
                public sealed class {{type.Model}}
                {
                """);
            foreach (var member in type.Members)
            {
                var initializer = member.NeedsNullDefault ? " = null!;" : "";
                builder.AppendLine($"    public {member.TypeDisplay} {member.Name} {{ get; init; }}{initializer}");
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
            // can be translated via ToScryRequest.
            builder.AppendLine(
                """
                    readonly global::Scry.Client.ScryClient client;
                    public ScryQuery(global::Scry.Client.ScryClient client) => this.client = client;
                """);
            foreach (var source in introspection.Sources)
            {
                builder.AppendLine($"    public global::System.Linq.IQueryable<{source.Model}> {source.Name} => client.Source<{source.Model}>(\"{source.Name}\");");
            }
        }
        else
        {
            // Completion only needs the shape — no Scry.Client dependency.
            foreach (var source in introspection.Sources)
            {
                builder.AppendLine($"    public global::System.Linq.IQueryable<{source.Model}> {source.Name} => null!;");
            }
        }

        builder.AppendLine("}");

        return builder.ToString();
    }
}
