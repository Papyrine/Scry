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

    static readonly DiagnosticDescriptor attachmentNotBytes = new(
        "SCRY004",
        "[Attachment] must be a byte[] member",
        "'{0}.{1}' carries [Attachment] but is not a byte[]. An attachment is a stream of bytes fetched on demand; apply it to a byte[] member, or remove it.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static readonly DiagnosticDescriptor attachmentNotEntity = new(
        "SCRY005",
        "[Attachment] is only valid on a queryable entity",
        "'{0}.{1}' carries [Attachment], but '{0}' is a {2} and has no primary key to fetch the value by. Expose the type with [Queryable], or remove the attachment.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static readonly DiagnosticDescriptor attachmentWithBinaryTransfer = new(
        "SCRY006",
        "[Attachment] cannot combine with [BinaryTransfer]",
        "'{0}.{1}' carries both [Attachment] and [BinaryTransfer]. [BinaryTransfer] changes how a value the query read is encoded; [Attachment] means the query never reads it. Keep one.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static readonly DiagnosticDescriptor attachmentKeysNotDerivable = new(
        "SCRY007",
        "Attachment keys are not derivable",
        "'{0}' carries an attachment but no primary key could be derived for it. An attachment is fetched by its row's key, so one must be nameable: mark the key member(s) with [Key], or name a member 'Id' or '{0}Id'.",
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
            invalid |= ValidateAttachments(context, source, extract);

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

    /// <summary>
    /// Reports every way an attachment can be misapplied, returning whether any fired. Each of these
    /// is refused again at server startup — the server validates the real model, not what a client was
    /// generated from — but a build error names the member while the model is still open in the editor.
    /// </summary>
    static bool ValidateAttachments(SourceProductionContext context, SourceInfo source, ModelExtract extract)
    {
        var invalid = false;
        foreach (var property in source.Properties)
        {
            if (!property.IsAttachment)
            {
                continue;
            }

            // TypeDisplay still holds the member's own type: an attachment is emitted as a handle
            // whatever it was declared as, so the declared type is the only thing that can say the
            // attribute was misapplied.
            if (property.TypeDisplay != "byte[]")
            {
                context.ReportDiagnostic(Diagnostic.Create(attachmentNotBytes, Location.None, source.ModelName, property.Name));
                invalid = true;
            }

            if (source.Kind != SourceKind.Entity)
            {
                var descriptor = source.Kind == SourceKind.Complex
                    ? "complex type"
                    : $"{source.Kind.ToString().ToLowerInvariant()} source";
                context.ReportDiagnostic(Diagnostic.Create(attachmentNotEntity, Location.None, source.ModelName, property.Name, descriptor));
                invalid = true;
            }

            if (property.HasBinaryTransfer)
            {
                context.ReportDiagnostic(Diagnostic.Create(attachmentWithBinaryTransfer, Location.None, source.ModelName, property.Name));
                invalid = true;
            }
        }

        // Read off the whole model rather than its own members: a derived type inherits its base's
        // attachment, and is fetched by its own key.
        if (Members(source, extract).Any(_ => _.IsAttachment) &&
            source.Keys.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(attachmentKeysNotDerivable, Location.None, source.ClrName));
            invalid = true;
        }

        return invalid;
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
            : $"[global::Scry.ScryModel({Arguments(source, extract)})]{Environment.NewLine}";

        var builder = Header();
        builder.AppendLine(
            $$"""
            /// <summary>Client query model for the '{{source.SourceName}}' {{descriptor}}.</summary>
            {{Obsolete(source.Obsolete)}}{{attribute}}public class {{source.ModelName}}{{inherits}}
            {
            """);
        foreach (var property in source.Properties)
        {
            var initializer = property.NeedsNullDefault || property.IsAttachment ? " = null!;" : "";
            builder.Append(Obsolete(property.Obsolete, indent: "    "));
            builder.AppendLine($"    public {Display(property)} {property.Name} {{ get; init; }}{initializer}");
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

    /// <summary>
    /// The type the generated member is declared as. Everything but an attachment is spelled as the
    /// model spelled it; an attachment becomes the handle, which is the whole point of the attribute
    /// and the reason — unlike <c>[BinaryTransfer]</c> — that it moves the schema stamp.
    /// </summary>
    /// <remarks>Mirrored by <c>Schema.DescribeMember</c>, which the schema stamp requires to agree.</remarks>
    static string Display(PropertyInfo property) =>
        property.IsAttachment ? "global::Scry.ScryAttachment" : property.TypeDisplay;

    // The scalar members a query written against this model projects when it writes no Select: the
    // ones it declares plus everything it inherits, base-first so the generated order matches the
    // declaration order a reader would expect. Navigations and collections are excluded — they are not
    // scalar leaves, matching the server's own default projection. So are attachments: the query never
    // reads one, which is why naming it in a projection is refused rather than silently dropped.
    static List<string> ScalarMembers(SourceInfo source, ModelExtract extract) =>
        Members(source, extract)
            .Where(_ => _ is {IsNavigation: false, IsCollection: false, IsAttachment: false})
            .Select(_ => _.Name)
            .ToList();

    // Every member the model exposes, inherited ones first. Mirrors MetadataModelReader.Inherited,
    // which walks the same chain while the extract is still being built.
    static List<PropertyInfo> Members(SourceInfo source, ModelExtract extract)
    {
        var members = new List<PropertyInfo>();
        if (source.BaseModelName is { } baseName &&
            extract.Sources.FirstOrDefault(_ => _.ModelName == baseName) is {ModelName: not null} baseSource)
        {
            members.AddRange(Members(baseSource, extract));
        }

        members.AddRange(source.Properties);
        return members;
    }

    // The [ScryModel] arguments: the source name and its scalar members, plus — only for a model
    // carrying an attachment — the key the attachment is fetched by and the attachment members
    // themselves. Both are omitted everywhere else, so a model without one is byte-identical to what
    // it was before attachments existed.
    static string Arguments(SourceInfo source, ModelExtract extract)
    {
        var members = ScalarMembers(source, extract);
        var written = string.Join(", ", new[] {source.SourceName}.Concat(members).Select(Literal));

        var attachments = Members(source, extract)
            .Where(_ => _.IsAttachment)
            .Select(_ => _.Name)
            .ToList();
        if (attachments.Count == 0)
        {
            return written;
        }

        return $"{written}, Keys = new[] {{{string.Join(", ", source.Keys.Select(Literal))}}}, Attachments = new[] {{{string.Join(", ", attachments.Select(Literal))}}}";
    }

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

            // The scalar members are passed along so a query that writes no Select still projects them by
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
            .Select(_ => (_.ModelName, _.BaseModelName, StampMembers(_)))
            .ToList();
        var enums = extract.Enums
            .Select(_ => (_.Name, _.Members.ToList()))
            .ToList();
        return SchemaStamp.Compute(sources, types, enums);
    }

    /// <summary>
    /// The members a type contributes to the stamp: its own, plus — for one carrying an attachment —
    /// a synthetic member naming the key that attachment is fetched by. The key is part of the
    /// client-visible contract only once something is fetched by it, so folding it in everywhere
    /// would report every deployed client as stale for a surface that did not change.
    /// </summary>
    /// <remarks>
    /// <c>~</c> cannot begin a C# identifier, so the synthetic name can never collide with a real
    /// member's. Mirrored by <c>Schema.StampMembers</c>.
    /// </remarks>
    static List<(string, string)> StampMembers(SourceInfo source)
    {
        var members = source.Properties
            .Select(_ => (_.Name, Display(_)))
            .ToList();
        if (source.Keys.Length > 0)
        {
            members.Add(("~keys", string.Join(" ", source.Keys)));
        }

        return members;
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
