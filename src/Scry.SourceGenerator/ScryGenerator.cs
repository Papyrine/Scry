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

    static DiagnosticDescriptor readFailed = new(
        "SCRY001",
        "Failed to read the Scry model assembly",
        "{0}",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static DiagnosticDescriptor duplicateSource = new(
        "SCRY002",
        "Duplicate Scry source name",
        "Two queryable types resolve to the source name '{0}'. Set a distinct [Queryable(Name = \"...\")] on one of them.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static DiagnosticDescriptor invalidSourceName = new(
        "SCRY003",
        "Scry source name cannot be a C# property name",
        "The source name '{0}' cannot be written as a C# property name, so the entry point exposing it cannot be generated. Set [Queryable(Name = \"...\")] to a plain identifier that is not a reserved keyword.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static DiagnosticDescriptor attachmentNotBytes = new(
        "SCRY004",
        "[Attachment] must be a byte[] member",
        "'{0}.{1}' carries [Attachment] but is not a byte[]. An attachment is a stream of bytes fetched on demand; apply it to a byte[] member, or remove it.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static DiagnosticDescriptor attachmentNotEntity = new(
        "SCRY005",
        "[Attachment] is only valid on a queryable entity",
        "'{0}.{1}' carries [Attachment], but '{0}' is a {2} and has no primary key to fetch the value by. Expose the type with [Queryable], or remove the attachment.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static DiagnosticDescriptor attachmentWithBinaryTransfer = new(
        "SCRY006",
        "[Attachment] cannot combine with [BinaryTransfer]",
        "'{0}.{1}' carries both [Attachment] and [BinaryTransfer]. [BinaryTransfer] changes how a value the query read is encoded; [Attachment] means the query never reads it. Keep one.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static DiagnosticDescriptor attachmentKeysNotDerivable = new(
        "SCRY007",
        "Attachment keys are not derivable",
        "'{0}' carries an attachment but no primary key could be derived for it. An attachment is fetched by its row's key, so one must be nameable: mark the key member(s) with [Key], or name a member 'Id' or '{0}Id'.",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    static DiagnosticDescriptor conflictingOptIn = new(
        "SCRY008",
        "A type opts in more than once",
        "{0}. A type opts in as exactly one of [Queryable], [QueryableView], [QueryablePoco], [QueryableComplex], or [Command].",
        "Scry",
        DiagnosticSeverity.Error,
        true);

    // The command diagnostics carry their whole message: the reader composes it, since only the reader
    // knows which of a rule's several failures it met.
    static Dictionary<string, DiagnosticDescriptor> commandProblems = new[]
        {
            Problem("SCRY009", "A command's target is not a queryable entity"),
            Problem("SCRY010", "A command's key is not bound"),
            Problem("SCRY011", "A generated name is used twice"),
            Problem("SCRY012", "Scry command name cannot be a C# member name"),
            Problem("SCRY013", "[QueryIgnore] on a command property"),
            Problem("SCRY014", "A command property is not a type a command can carry"),
            Problem("SCRY015", "A command's result is not a class a result can be"),
            Problem("SCRY016", "A capability collides with a member of its target"),
            Problem("SCRY017", "A command is not a concrete class")
        }
        .ToDictionary(_ => _.Id, StringComparer.Ordinal);

    static DiagnosticDescriptor Problem(string id, string title) =>
        new(id, title, "{0}", "Scry", DiagnosticSeverity.Error, true);

    static void Emit(SourceProductionContext context, ModelExtract extract)
    {
        if (extract.Error is { } error)
        {
            context.ReportDiagnostic(Diagnostic.Create(readFailed, Location.None, error));
            return;
        }

        // Refused at startup by the server too, and nothing is emitted: the surface would be one the
        // server does not agree with, which a client would only learn as a stale stamp.
        if (extract.Conflicts.Length > 0)
        {
            foreach (var conflict in extract.Conflicts)
            {
                context.ReportDiagnostic(Diagnostic.Create(conflictingOptIn, Location.None, conflict));
            }

            return;
        }

        // A misdeclared command is refused at startup by the server too, and nothing is emitted for the
        // same reason a conflicting opt-in emits nothing.
        if (extract.Problems.Length > 0)
        {
            foreach (var problem in extract.Problems)
            {
                context.ReportDiagnostic(Diagnostic.Create(commandProblems[problem.Id], Location.None, problem.Message));
            }

            return;
        }

        if (extract.Sources.Length == 0 &&
            extract.Commands.Length == 0)
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

        invalid |= ValidateGeneratedNames(context, extract);

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

        if (extract.Commands.Length > 0)
        {
            context.AddSource("ScryCommands.g.cs", EmitCommands(extract));
        }

        context.AddSource("ScryQuery.g.cs", EmitQuery(extract));
    }

    /// <summary>
    /// Reports a name the generated code would declare twice. Commands, results, query models and enums
    /// are all classes in one namespace beside <c>ScryQuery</c> and <c>ScryCommands</c>, and the facade
    /// declares a method and a <c>Can</c> property per command — emitting either twice would surface as
    /// a compile error in code the consumer cannot edit.
    /// </summary>
    /// <remarks>
    /// A model with no commands declares nothing new here, and its own duplicates are reported as SCRY002
    /// above, so this only ever speaks about a model that has commands.
    /// </remarks>
    static bool ValidateGeneratedNames(SourceProductionContext context, ModelExtract extract)
    {
        if (extract.Commands.Length == 0)
        {
            return false;
        }

        var types = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ScryQuery"] = "the query entry point",
            ["ScryCommands"] = "the command facade"
        };
        var invalid = false;

        void Declare(Dictionary<string, string> declared, string name, string what)
        {
            if (declared.TryGetValue(name, out var previous))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        commandProblems["SCRY011"],
                        Location.None,
                        $"The name '{name}' is generated twice: as {previous} and as {what}. Every command, result class, query model and enum is emitted into Scry.Generated, and every command adds a method and a 'Can' property to the facade, so each needs a name of its own. Set [Command(Name = \"...\")] on the command, or rename one of the two."));
                invalid = true;
                return;
            }

            declared[name] = what;
        }

        // Two query models sharing a name were reported as SCRY002 above, so one taking the other's place
        // here is not reported again.
        foreach (var source in extract.Sources)
        {
            if (!types.ContainsKey(source.ModelName))
            {
                types[source.ModelName] = $"the query model for '{source.SourceName}'";
            }
        }

        foreach (var enumeration in extract.Enums)
        {
            Declare(types, enumeration.Name, "an enum");
        }

        foreach (var result in extract.Results)
        {
            Declare(types, result.Name, "a result class");
        }

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in extract.Commands)
        {
            Declare(types, command.Name, $"the command '{command.ClrName}'");
            Declare(members, command.Name, $"the facade method for '{command.ClrName}'");
            Declare(members, $"Can{command.Name}", $"the facade capability for '{command.ClrName}'");
        }

        return invalid;
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
            {{Obsolete(source.Obsolete)}}{{Sensitive(source.IsSensitive)}}{{attribute}}public class {{source.ModelName}}{{inherits}}
            {
            """);
        foreach (var property in source.Properties)
        {
            var initializer = property.NeedsNullDefault || property.IsAttachment ? " = null!;" : "";
            if (property.Capability is { } command)
            {
                builder.AppendLine($"    /// <summary>Whether this caller may send '{command}' against this row, as the server's policy for it decides.</summary>");
            }

            builder.Append(Obsolete(property.Obsolete, indent: "    "));
            builder.Append(Sensitive(property.IsSensitive, indent: "    "));
            builder.AppendLine($"    public {Display(property)} {CSharpIdentifier.Escape(property.Name)} {{ get; init; }}{initializer}");
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
    /// The <c>[ScrySensitive]</c> line a model type or member the server marked <c>[Sensitive]</c> is
    /// preceded by, and empty for everything else. Unlike <c>[Obsolete]</c> this one is not advisory: it
    /// is what a client reads to decide that a query has to travel in a body, and it moves the schema
    /// stamp because it changes what an already-deployed client is allowed to do.
    /// </summary>
    static string Sensitive(bool sensitive, string indent = "") =>
        sensitive ? $"{indent}[global::Scry.ScrySensitive]{Environment.NewLine}" : "";

    /// <summary>
    /// The type the generated member is declared as. Everything but an attachment is spelled as the
    /// model spelled it; an attachment becomes the handle, which is the whole point of the attribute
    /// and the reason — unlike <c>[BinaryTransfer]</c> — that it moves the schema stamp.
    /// </summary>
    /// <remarks>Mirrored by <c>Schema.DescribeMember</c>, which the schema stamp requires to agree.</remarks>
    internal static string Display(PropertyInfo property) =>
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
            // Values, the underlying type and [Flags] are carried across so a member means on the
            // client exactly what it means on the server — a combined flag travels by name, and the
            // name it resolves to is decided by these.
            if (enumeration.IsFlags)
            {
                builder.AppendLine("[global::System.Flags]");
            }

            var underlying = enumeration.Underlying == "int" ? "" : $" : {enumeration.Underlying}";
            builder.AppendLine($"public enum {enumeration.Name}{underlying}");
            builder.AppendLine("{");
            var members = enumeration.Members.ToList();
            var values = enumeration.Values.ToList();
            for (var i = 0; i < members.Count; i++)
            {
                builder.AppendLine($"    {CSharpIdentifier.Escape(members[i])} = {values[i]},");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// The classes a client sends commands as and reads results into, and the facade sending them. Each
    /// command class carries <c>[ScryCommand]</c>, which is what names it on the wire; the facade's
    /// methods are plain instance methods returning a task, so any .NET language can call them.
    /// </summary>
    static string EmitCommands(ModelExtract extract)
    {
        var builder = Header();
        foreach (var command in extract.Commands)
        {
            builder.AppendLine($"/// <summary>The '{command.Name}' command, sent through <c>ScryCommands</c>.</summary>");
            builder.Append(Obsolete(command.Obsolete));
            builder.AppendLine($"[global::Scry.ScryCommand({CommandArguments(command)})]");
            builder.AppendLine($"public sealed class {command.Name}");
            builder.AppendLine("{");
            AppendValueProperties(builder, command.Properties);
            builder.AppendLine("}");
            builder.AppendLine();
        }

        foreach (var result in extract.Results)
        {
            builder.AppendLine($"/// <summary>What a command answers with: '{result.Name}'.</summary>");
            builder.AppendLine($"public sealed class {result.Name}");
            builder.AppendLine("{");
            AppendValueProperties(builder, result.Properties);
            builder.AppendLine("}");
            builder.AppendLine();
        }

        builder.AppendLine(
            """
            /// <summary>The commands this client may send, each answered with its outcome.</summary>
            public sealed class ScryCommands
            {
                global::Scry.ScryClient client;

                public ScryCommands(global::Scry.ScryClient client) =>
                    this.client = client;
            """);
        foreach (var command in extract.Commands)
        {
            // Fully qualified: the method and the class it sends share a name, and inside this class the
            // bare name is the method.
            var type = $"global::Scry.Generated.{command.Name}";
            builder.AppendLine();
            builder.AppendLine($"    /// <summary>Sends '{command.Name}', answering with its outcome.</summary>");
            builder.Append(Obsolete(command.Obsolete, indent: "    "));
            if (command.ResultName is { } resultName)
            {
                var result = $"global::Scry.Generated.{resultName}";
                builder.AppendLine(
                    $"""
                        public global::System.Threading.Tasks.Task<global::Scry.ScryCommandOutcome<{result}>> {command.Name}(
                            {type} command,
                            global::System.Threading.CancellationToken cancel = default) =>
                            client.SendCommandAsync<{type}, {result}>(command, cancel);
                    """);
            }
            else
            {
                builder.AppendLine(
                    $"""
                        public global::System.Threading.Tasks.Task<global::Scry.ScryCommandOutcome> {command.Name}(
                            {type} command,
                            global::System.Threading.CancellationToken cancel = default) =>
                            client.SendCommandAsync(command, cancel);
                    """);
            }

            builder.AppendLine();
            builder.AppendLine(
                $"""
                    /// <summary>
                    /// Whether this caller may send '{command.Name}' at all, as the server last said. Advisory:
                    /// the server decides again on every command. False until the server has answered.
                    /// </summary>
                    public bool Can{command.Name} => client.Can({Literal(command.Name)});
                """);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    // The [ScryCommand] arguments: the wire name, and for a targeted command the source it acts on and
    // the payload properties carrying that source's key, and the result class where there is one.
    static string CommandArguments(CommandInfo command)
    {
        var arguments = new List<string> {Literal(command.Name)};
        if (command.Target is { } target)
        {
            arguments.Add($"Target = {Literal(target)}");
            arguments.Add($"Keys = new[] {{{string.Join(", ", command.Keys.Select(Literal))}}}");
        }

        if (command.ResultName is { } result)
        {
            arguments.Add($"Result = typeof(global::Scry.Generated.{result})");
        }

        return string.Join(", ", arguments);
    }

    static void AppendValueProperties(StringBuilder builder, EquatableArray<PropertyInfo> properties)
    {
        foreach (var property in properties)
        {
            var initializer = property.NeedsNullDefault ? " = null!;" : "";
            builder.Append(Obsolete(property.Obsolete, indent: "    "));
            builder.AppendLine($"    public {property.TypeDisplay} {CSharpIdentifier.Escape(property.Name)} {{ get; init; }}{initializer}");
        }
    }

    static string EmitQuery(ModelExtract extract)
    {
        var builder = Header();
        var commands = extract.Commands.Length > 0;
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

                global::Scry.ScryClient client;

                public ScryQuery(global::Scry.ScryClient client)
                {
                    this.client = client;
                    client.SchemaStamp = SchemaStamp;{{(commands ? $"{Environment.NewLine}        Commands = new(client);" : "")}}
                }
            """);
        if (commands)
        {
            builder.AppendLine(
                """

                    /// <summary>The commands this client may send, each answered with its outcome.</summary>
                    public ScryCommands Commands { get; }
                """);
        }
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
    internal static string ComputeStamp(ModelExtract extract)
    {
        var sources = extract.Sources
            .Where(_ => _.Kind != SourceKind.Complex)
            .Select(_ => (_.SourceName, _.Kind.ToString(), _.ModelName))
            .ToList();
        var types = extract.Sources
            .Select(_ => (_.ModelName, _.BaseModelName, StampMembers(_)))
            .ToList();
        var enums = extract.Enums
            .Select(_ => (
                _.Name,
                _.Underlying,
                _.IsFlags,
                _.Members.ToList().Zip(_.Values.ToList(), (name, value) => (name, value)).ToList()))
            .ToList();
        var commands = extract.Commands
            .Select(_ => (_.Name, _.Target, StampCommandMembers(_)))
            .ToList();
        var results = extract.Results
            .Select(_ => (_.Name, _.Properties.Select(property => (property.Name, property.TypeDisplay)).ToList()))
            .ToList();
        return SchemaStamp.Compute(sources, types, enums, commands, results);
    }

    /// <summary>
    /// The members a command contributes to the stamp: its payload, plus synthetic members naming the
    /// payload properties its target's key is bound to and the class it answers with. Mirrored by
    /// <c>Schema.StampCommandMembers</c>, byte for byte.
    /// </summary>
    static List<(string, string)> StampCommandMembers(CommandInfo command)
    {
        var members = command.Properties
            .Select(_ => (_.Name, _.TypeDisplay))
            .ToList();
        if (command.Keys.Length > 0)
        {
            members.Add(("~keys", string.Join(" ", command.Keys)));
        }

        if (command.ResultName is { } result)
        {
            members.Add(("~result", result));
        }

        return members;
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
            members.Add(("~keys", string.Join(' ', source.Keys)));
        }

        if (Sensitivity(source) is { Length: > 0 } sensitive)
        {
            members.Add(("~sensitive", sensitive));
        }

        return members;
    }

    /// <summary>
    /// The sensitivity line a type contributes to the stamp: the members it marks, and <c>*</c> where
    /// the type itself is marked. Present only where something is, so a model that marks nothing hashes
    /// exactly as it did before <c>[Sensitive]</c> existed.
    /// </summary>
    /// <remarks>
    /// Hashed — unlike <c>[Obsolete]</c> — because it changes what an already-deployed client may do
    /// rather than only what it should be told. A client generated before a member was marked keeps
    /// asking in URLs and is refused; moving the stamp is what turns that into a reported staleness
    /// with a regenerate to fix it. <c>*</c> is not a member name, so it cannot collide with one.
    /// </remarks>
    static string Sensitivity(SourceInfo source)
    {
        var names = source.Properties
            .Where(_ => _.IsSensitive)
            .Select(_ => _.Name)
            .ToList();
        if (source.IsSensitive)
        {
            names.Insert(0, "*");
        }

        return string.Join(' ', names);
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
