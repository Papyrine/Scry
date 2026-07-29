namespace Scry.SourceGenerator;

/// <summary>
/// Reads the server model assembly (pointed at by the <c>ScryModelDll</c> build property) and
/// generates strongly-typed client query DTOs, re-emitted enums, and a query entry point. The
/// assembly is read as metadata only — never referenced, loaded, or executed.
/// </summary>
[Generator]
public class ScryGenerator :
    IIncrementalGenerator
{
    const string generatedNamespace = "Scry.Generated";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var path = context.AnalyzerConfigOptionsProvider
            .Select((provider, _) => GetProperty(provider, "build_property.ScryModelDll"));

        // The DLL is read out of band, so Roslyn cannot see content changes from the path alone.
        // A stamp property (a content hash set by the build targets) folds content into the pipeline
        // input so the model is re-read exactly when it changes.
        var stamp = context.AnalyzerConfigOptionsProvider
            .Select((provider, _) => GetProperty(provider, "build_property.ScryModelStamp"));

        var model = path
            .Combine(stamp)
            .Select((pair, _) => MetadataModelReader.Read(pair.Left));

        context.RegisterSourceOutput(model, Emit);
    }

    static string? GetProperty(AnalyzerConfigOptionsProvider provider, string key)
    {
        if (provider.GlobalOptions.TryGetValue(key, out var value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    static readonly DiagnosticDescriptor readFailed = new(
        "SCRY001",
        "Failed to read the Scry model assembly",
        "{0}",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static readonly DiagnosticDescriptor duplicateSource = new(
        "SCRY002",
        "Duplicate Scry source name",
        "Two queryable types resolve to the source name '{0}'. Set a distinct [Queryable(Name = \"...\")] on one of them.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static void Emit(SourceProductionContext context, ModelExtract extract)
    {
        if (extract.Error is { } error)
        {
            context.ReportDiagnostic(Diagnostic.Create(readFailed, Location.None, error));
            return;
        }

        if (extract.Sources.Length == 0)
        {
            return;
        }

        // Emitting duplicates would otherwise surface as a CS0102 on generated code the user cannot
        // see. The server rejects the same clash at startup. Two axes clash independently: the
        // generated model class name (all types, incl. complex) and the entry-point property name
        // (sources only — complex types emit no entry point).
        var seenModels = new HashSet<string>(StringComparer.Ordinal);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = false;
        foreach (var source in extract.Sources)
        {
            if (!seenModels.Add(source.ModelName))
            {
                context.ReportDiagnostic(Diagnostic.Create(duplicateSource, Location.None, source.SourceName));
                duplicated = true;
            }

            if (source.Kind != SourceKind.Complex &&
                !seenSources.Add(source.SourceName))
            {
                context.ReportDiagnostic(Diagnostic.Create(duplicateSource, Location.None, source.SourceName));
                duplicated = true;
            }
        }

        if (duplicated)
        {
            return;
        }

        foreach (var source in extract.Sources)
        {
            context.AddSource($"{source.ModelName}.g.cs", EmitModel(source));
        }

        if (extract.Enums.Length > 0)
        {
            context.AddSource("ScryEnums.g.cs", EmitEnums(extract.Enums));
        }

        context.AddSource("ScryQuery.g.cs", EmitQuery(extract));
    }

    static string EmitModel(SourceInfo source)
    {
        var descriptor = source.Kind == SourceKind.Complex
            ? "complex type"
            : $"{source.Kind.ToString().ToLowerInvariant()} source";
        var builder = Header();
        builder.AppendLine(
            $$"""
            /// <summary>Client query model for the '{{source.SourceName}}' {{descriptor}}.</summary>
            public sealed class {{source.ModelName}}
            {
            """);
        foreach (var property in source.Properties)
        {
            var initializer = property.NeedsNullDefault ? " = null!;" : "";
            builder.AppendLine($"    public {property.TypeDisplay} {property.Name} {{ get; init; }}{initializer}");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    static string EmitEnums(EquatableArray<EnumInfo> enums)
    {
        var builder = Header();
        foreach (var enumeration in enums)
        {
            builder.AppendLine(
                $$"""
                public enum {{enumeration.Name}}
                {
                """);
            foreach (var member in enumeration.Members)
            {
                builder.AppendLine($"    {member},");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    static string EmitQuery(ModelExtract extract)
    {
        var builder = Header();
        builder.AppendLine(
            $$"""
            /// <summary>Entry point for writing LINQ queries against the allow-listed sources.</summary>
            public sealed class ScryQuery
            {
                /// <summary>
                /// A hash of the queryable surface this client was generated against. Attached to each
                /// request so the server can identify a client generated against a different model.
                /// </summary>
                public const string SchemaStamp = "{{ComputeStamp(extract)}}";

                readonly global::Scry.Client.ScryClient client;

                public ScryQuery(global::Scry.Client.ScryClient client)
                {
                    this.client = client;
                    client.SchemaStamp = SchemaStamp;
                }
            """);
        foreach (var source in extract.Sources)
        {
            // Complex types are traversable member types, not roots — they get no entry point.
            if (source.Kind == SourceKind.Complex)
            {
                continue;
            }

            // The scalar members ride along so a query that writes no Select still projects them by
            // name. That keeps the response keyed by the names this client was generated with, rather
            // than whatever the server's current model calls them.
            var members = string.Join(
                ", ",
                source.Properties.Where(_ => !_.IsNavigation && !_.IsCollection).Select(_ => $"\"{_.Name}\""));

            builder.AppendLine();
            builder.AppendLine(
                $"""
                public global::System.Linq.IQueryable<{source.ModelName}> {source.SourceName} =>
                    client.Source<{source.ModelName}>("{source.SourceName}", [{members}]);
            """);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    // Mirrors Schema.ComputeStamp on the server: same canonical inputs into the shared SchemaStamp,
    // so a client generated from the same surface carries the same stamp the server computes.
    static string ComputeStamp(ModelExtract extract)
    {
        var sources = extract.Sources
            .Where(_ => _.Kind != SourceKind.Complex)
            .Select(_ => (_.SourceName, _.Kind.ToString(), _.ModelName))
            .ToList();
        var types = extract.Sources
            .Select(_ => (_.ModelName, _.Properties.Select(property => (property.Name, property.TypeDisplay)).ToList()))
            .ToList();
        var enums = extract.Enums
            .Select(_ => (_.Name, _.Members.ToList()))
            .ToList();
        return SchemaStamp.Compute(sources, types, enums);
    }

    static StringBuilder Header()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"""
            // <auto-generated/>
            #nullable enable
            namespace {generatedNamespace};
            """);
        builder.AppendLine();
        return builder;
    }
}
