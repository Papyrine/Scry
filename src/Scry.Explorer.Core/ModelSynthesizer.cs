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
            // Mirrors the generator: not sealed, and inheriting where the server's types do, so a
            // snippet can narrow with OfType exactly as generated client code can.
            var inherits = type.Base is null ? "" : $" : {type.Base}";
            var attribute = executable && introspection.Sources.Any(_ => _.Model == type.Model)
                ? $"[global::Scry.Client.ScryModel({Arguments(type, introspection)})]{Environment.NewLine}"
                : "";

            builder.AppendLine(
                $$"""
                {{attribute}}public class {{type.Model}}{{inherits}}
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
            // can be translated via ToScryRequest, and carries the same schema stamp — here taken
            // from the introspection the explorer just fetched.
            builder.AppendLine(
                $$"""
                    public const string SchemaStamp = "{{introspection.SchemaStamp}}";

                    readonly global::Scry.Client.ScryClient client;

                    public ScryQuery(global::Scry.Client.ScryClient client)
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
                        .Select(_ => $"\"{_}\""));
                builder.AppendLine($"    public global::System.Linq.IQueryable<{source.Model}> {source.Name} => client.Source<{source.Model}>(\"{source.Name}\", [{members}]);");
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

        members.AddRange(
            type.Members
                .Where(_ => _ is {IsNavigation: false, IsCollection: false})
                .Select(_ => _.Name));
        return members;
    }

    static string Arguments(ScryTypeInfo type, ScryIntrospection introspection)
    {
        var source = introspection.Sources.First(_ => _.Model == type.Model).Name;
        return string.Join(", ", new[] {source}.Concat(ScalarMembers(type, introspection)).Select(_ => $"\"{_}\""));
    }
}
