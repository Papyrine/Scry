using System.Text;
using Scry.Wire;

namespace Scry.Explorer.Ui.Roslyn;

/// <summary>
/// Turns a <see cref="ScryIntrospection"/> into the C# source the design-time generator would emit:
/// the re-emitted enums, one query-model class per type, and a <c>ScryQuery</c> facade exposing each
/// source as <see cref="IQueryable{T}"/>. Compiled in-browser so Roslyn can complete against it.
/// </summary>
static class ModelSynthesizer
{
    public static string Synthesize(ScryIntrospection introspection)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#nullable enable");
        builder.AppendLine("namespace Scry.Generated;");
        builder.AppendLine();

        foreach (var enumeration in introspection.Enums)
        {
            builder.AppendLine($"public enum {enumeration.Name}");
            builder.AppendLine("{");
            foreach (var value in enumeration.Values)
            {
                builder.AppendLine($"    {value},");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        foreach (var type in introspection.Types)
        {
            builder.AppendLine($"public sealed class {type.ModelName}");
            builder.AppendLine("{");
            foreach (var member in type.Members)
            {
                var initializer = member.NeedsNullDefault ? " = null!;" : "";
                builder.AppendLine($"    public {member.TypeDisplay} {member.Name} {{ get; init; }}{initializer}");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        // For completion the facade only needs the right shape; execution (Stage 2) uses the real
        // client-backed ScryQuery.
        builder.AppendLine("public sealed class ScryQuery");
        builder.AppendLine("{");
        foreach (var source in introspection.Sources)
        {
            builder.AppendLine($"    public global::System.Linq.IQueryable<{source.ModelName}> {source.Name} => null!;");
        }

        builder.AppendLine("}");

        return builder.ToString();
    }
}
