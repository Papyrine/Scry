/// <summary>
/// The description a server gives of its model, made from the model's DLL read as metadata: what
/// <c>ScryProcessor.Describe()</c> answers, for a client generated at build time with no server
/// started. The surface is the model's alone, read by the source generator's own reader and stamped by
/// its own stamp, which is why a client generated from this is the one the server validates. Only the
/// two limits are the server's configuration, so they are given.
/// </summary>
/// <remarks>
/// Mirrors <c>Schema.DescribeSurface</c> member for member, in the same orders and with the same
/// spellings; <c>ModelDescriptionTests</c> compares the two byte for byte over the test model.
/// </remarks>
static class ModelDescriber
{
    const string list = "global::System.Collections.Generic.IReadOnlyList<";
    const string queryModel = "QueryModel";

    /// <summary>What a model whose reading failed was refused for, one line each; empty where it was not.</summary>
    public static IReadOnlyList<string> Problems(ModelExtract extract)
    {
        var problems = new List<string>();
        if (extract.Error is { } error)
        {
            problems.Add(error);
        }

        problems.AddRange(extract.Conflicts);
        problems.AddRange(extract.Generics);
        problems.AddRange(extract.Problems.Select(_ => $"{_.Id}: {_.Message}"));
        return problems;
    }

    public static ScryIntrospection Describe(ModelExtract extract, int maxPageSize, int queryUrlLimit)
    {
        var types = extract.Sources
            .OrderBy(ClrName, StringComparer.Ordinal)
            .Select(Type)
            .ToList();
        var sources = extract.Sources
            .Where(_ => _.Kind != SourceKind.Complex)
            .OrderBy(_ => _.SourceName, StringComparer.Ordinal)
            .Select(_ =>
                new ScrySourceInfo(_.SourceName, _.Kind.ToString(), _.ModelName)
                {
                    Obsolete = _.Obsolete
                })
            .ToList();
        var enums = extract.Enums
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .Select(_ =>
                new ScryEnumInfo(_.Name, [.. _.Members])
                {
                    Constants = [.. _.Values],
                    IsFlags = _.IsFlags,
                    Underlying = _.Underlying
                })
            .ToList();
        var commands = extract.Commands
            .OrderBy(_ => _.Name, StringComparer.Ordinal)
            .Select(_ => Command(_, extract))
            .ToList();

        return new(ScryIntrospection.CurrentVersion, maxPageSize, sources, types, enums)
        {
            SchemaStamp = ModelStamp.Compute(extract),
            QueryUrlLimit = queryUrlLimit,
            Commands = commands
        };
    }

    // The server orders types by the CLR type's own name, which a model is named after.
    static string ClrName(SourceInfo source) =>
        source.ModelName[..^queryModel.Length];

    static ScryTypeInfo Type(SourceInfo source)
    {
        IReadOnlyList<string>? keys = null;
        if (source.Keys.Length > 0)
        {
            keys = [.. source.Keys];
        }

        return new(
            source.ModelName,
            [.. source.Properties.OrderBy(_ => _.Name, StringComparer.Ordinal).Select(Member)])
        {
            Base = source.BaseModelName,
            Obsolete = source.Obsolete,
            IsSensitive = source.IsSensitive,
            // Carried only where something fetches by it, as the server carries it.
            Keys = keys,
            Declared = [.. source.Properties.Select(_ => _.Name)]
        };
    }

    // A capability is backed by no property, so it has no deprecation or sensitivity to publish.
    static ScryMemberInfo Member(PropertyInfo property)
    {
        if (property.Capability is { } command)
        {
            return new(property.Name, "bool", NeedsNullDefault: false, IsNavigation: false)
            {
                IsCapability = true,
                Command = command
            };
        }

        return Shape(property) with
        {
            Obsolete = property.Obsolete,
            IsSensitive = property.IsSensitive
        };
    }

    static ScryMemberInfo Shape(PropertyInfo property)
    {
        if (property.IsCollection)
        {
            return new(property.Name, property.TypeDisplay, NeedsNullDefault: true, IsNavigation: false, IsCollection: true);
        }

        if (property.IsNavigation)
        {
            return new(property.Name, property.TypeDisplay, NeedsNullDefault: false, IsNavigation: true);
        }

        if (property.IsAttachment)
        {
            return new(property.Name, ModelStamp.Display(property), NeedsNullDefault: true, IsNavigation: false)
            {
                IsAttachment = true,
                ContentType = property.ContentType
            };
        }

        return new(property.Name, property.TypeDisplay, NeedsReferenceDefault(property.TypeDisplay), IsNavigation: false);
    }

    static ScryCommandInfo Command(CommandInfo command, ModelExtract extract)
    {
        IReadOnlyList<string>? keys = null;
        if (command.Target is not null)
        {
            keys = [.. command.Keys];
        }

        ScryResultInfo? result = null;
        if (command.ResultName is { } name)
        {
            var answered = extract.Results.Single(_ => _.Name == name);
            result = new(answered.Name, Values(answered.Properties));
        }

        return new(command.Name, Values(command.Properties))
        {
            Target = command.Target,
            Keys = keys,
            Result = result,
            Obsolete = command.Obsolete
        };
    }

    // A payload's or a result's properties: values or lists of them, never navigations, and a list never
    // marked a collection, as the server describes them.
    static List<ScryMemberInfo> Values(EquatableArray<PropertyInfo> properties) =>
        [
            .. properties
                .OrderBy(_ => _.Name, StringComparer.Ordinal)
                .Select(_ =>
                    new ScryMemberInfo(_.Name, _.TypeDisplay, NeedsReferenceDefault(_.TypeDisplay) || _.TypeDisplay.StartsWith(list, StringComparison.Ordinal), IsNavigation: false)
                    {
                        Obsolete = _.Obsolete
                    })
        ];

    // A non-nullable reference type needs '= null!' in generated code, which the description says.
    static bool NeedsReferenceDefault(string display) =>
        display is "string" or "byte[]";
}
