namespace Scry;

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

    static readonly DiagnosticDescriptor invalidSourceName = new(
        "SCRY003",
        "Scry source name cannot be a C# property name",
        "The source name '{0}' cannot be written as a C# property name, so the entry point exposing it cannot be generated. Set [Queryable(Name = \"...\")] to a plain identifier that is not a reserved keyword.",
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

        // Both checks below catch a model that would emit code the user cannot see to fix, and both
        // are refused at startup by the server too. Emitting duplicates would surface as a CS0102;
        // emitting a source name that is not an identifier would not parse at all. Nothing is emitted
        // when either fires, so the consumer sees the reported cause rather than its consequences.
        //
        // Duplicates clash on two axes independently: the generated model class name (all types, incl.
        // complex) and the entry-point property name (sources only — complex types emit no entry
        // point). The identifier rule likewise applies to source names only, since a model name is
        // derived from the CLR type name and a complex type has no entry point to name.
        var seenModels = new HashSet<string>(StringComparer.Ordinal);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var invalid = false;
        foreach (var source in extract.Sources)
        {
            if (!seenModels.Add(source.ModelName))
            {
                context.ReportDiagnostic(Diagnostic.Create(duplicateSource, Location.None, source.SourceName));
                invalid = true;
            }

            if (source.Kind == SourceKind.Complex)
            {
                continue;
            }

            if (!seenSources.Add(source.SourceName))
            {
                context.ReportDiagnostic(Diagnostic.Create(duplicateSource, Location.None, source.SourceName));
                invalid = true;
            }

            if (!CSharpIdentifier.IsValid(source.SourceName))
            {
                context.ReportDiagnostic(Diagnostic.Create(invalidSourceName, Location.None, source.SourceName));
                invalid = true;
            }
        }

        if (invalid)
        {
            return;
        }

        foreach (var source in extract.Sources)
        {
            context.AddSource($"{source.ModelName}.g.cs", EmitModel(source, extract));
        }

        if (extract.Enums.Length > 0)
        {
            context.AddSource("ScryEnums.g.cs", EmitEnums(extract.Enums));
        }

        context.AddSource("ScryQuery.g.cs", EmitQuery(extract));
    }

    static string EmitModel(SourceInfo source, ModelExtract extract)
    {
        var descriptor = source.Kind == SourceKind.Complex
            ? "complex type"
            : $"{source.Kind.ToString().ToLowerInvariant()} source";
        // Not sealed, and inheriting where the CLR type does: OfType on the client is only expressible
        // if the generated models carry the same derivation the server's types do.
        var inherits = source.BaseModelName is null ? "" : $" : {source.BaseModelName}";

        // A complex type is a member type, not a source, so it has no wire name to carry.
        var attribute = source.Kind == SourceKind.Complex
            ? ""
            : $"[global::Scry.ScryModel({Arguments(source.SourceName, ScalarMembers(source, extract))})]{Environment.NewLine}";

        var builder = Header();
        builder.AppendLine(
            $$"""
            /// <summary>Client query model for the '{{source.SourceName}}' {{descriptor}}.</summary>
            {{Obsolete(source.Obsolete)}}{{attribute}}public class {{source.ModelName}}{{inherits}}
            {
            """);
        foreach (var property in source.Properties)
        {
            var initializer = property.NeedsNullDefault ? " = null!;" : "";
            builder.Append(Obsolete(property.Obsolete, indent: "    "));
            builder.AppendLine($"    public {property.TypeDisplay} {property.Name} {{ get; init; }}{initializer}");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// The <c>[Obsolete]</c> line a deprecated model type, member, or entry point is preceded by —
    /// empty when the model did not deprecate it. Advisory only: the server still executes queries
    /// against an obsolete member, so this warns rather than blocks, and it never reaches the schema
    /// stamp, since deprecating something does not change the queryable surface.
    /// </summary>
    static string Obsolete(string? message, string indent = "")
    {
        if (message is null)
        {
            return "";
        }

        // An empty message is a bare [Obsolete] on the model, which has nothing to say beyond the fact.
        var arguments = message.Length == 0 ? "" : $"({Literal(message)})";
        return $"{indent}[global::System.ObsoleteAttribute{arguments}]{Environment.NewLine}";
    }

    // The scalar members a query written against this model projects when it writes no Select: the
    // ones it declares plus everything it inherits, base-first so the generated order matches the
    // declaration order a reader would expect. Navigations and collections are excluded — they are not
    // scalar leaves, matching the server's own default projection.
    static List<string> ScalarMembers(SourceInfo source, ModelExtract extract)
    {
        var members = new List<string>();
        if (source.BaseModelName is { } baseName &&
            extract.Sources.FirstOrDefault(_ => _.ModelName == baseName) is {ModelName: not null} baseSource)
        {
            members.AddRange(ScalarMembers(baseSource, extract));
        }

        members.AddRange(
            source.Properties
                .Where(_ => _ is {IsNavigation: false, IsCollection: false})
                .Select(_ => _.Name));
        return members;
    }

    static string Arguments(string source, List<string> members) =>
        string.Join(", ", new[] {source}.Concat(members).Select(Literal));

    // Every string the model contributes reaches generated code as a literal — a source name, a member
    // name, an [Obsolete] message. Only the last is free text, but none of them are escaped at the
    // source, so they are all formatted rather than interpolated between bare quotes.
    static string Literal(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

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

                readonly global::Scry.ScryClient client;

                public ScryQuery(global::Scry.ScryClient client)
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
            var members = string.Join(", ", ScalarMembers(source, extract).Select(Literal));

            builder.AppendLine();
            // The entry point is where a query against a deprecated source starts, so it carries the
            // deprecation too — a client writing 'Query.Employee' sees it without traversing a member.
            builder.Append(Obsolete(source.Obsolete, indent: "    "));
            builder.AppendLine(
                $"""
                public global::System.Linq.IQueryable<{source.ModelName}> {source.SourceName} =>
                    client.Source<{source.ModelName}>({Literal(source.SourceName)}, [{members}]);
            """);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    // Mirrors Schema.ComputeStamp on the server: same canonical inputs into the shared SchemaStamp,
    // so a client generated from the same surface carries the same stamp the server computes.
    // Deprecation is deliberately absent: marking something [Obsolete] leaves the queryable surface
    // exactly as it was, and folding it in would report every deployed client as stale for what is
    // only a note to whoever next rebuilds one.
    static string ComputeStamp(ModelExtract extract)
    {
        var sources = extract.Sources
            .Where(_ => _.Kind != SourceKind.Complex)
            .Select(_ => (_.SourceName, _.Kind.ToString(), _.ModelName))
            .ToList();
        var types = extract.Sources
            .Select(_ => (_.ModelName, _.BaseModelName, _.Properties.Select(property => (property.Name, property.TypeDisplay)).ToList()))
            .ToList();
        var enums = extract.Enums
            .Select(_ => (_.Name, _.Members.ToList()))
            .ToList();
        return SchemaStamp.Compute(sources, types, enums);
    }

    static StringBuilder Header()
    {
        var builder = new StringBuilder();
        // The obsolete warnings are suppressed inside generated code only. A deprecated model type is
        // still referenced here — by every navigation to it and by its own entry point — and
        // '<auto-generated/>' does not suppress CS0612/CS0618, so a consumer building with
        // TreatWarningsAsErrors would fail on code it cannot edit. Uses in the consumer's own query
        // code, which is where the deprecation is worth reporting, still warn.
        builder.AppendLine(
            $"""
            // <auto-generated/>
            #nullable enable
            #pragma warning disable CS0612, CS0618
            namespace {generatedNamespace};
            """);
        builder.AppendLine();
        return builder;
    }
}
