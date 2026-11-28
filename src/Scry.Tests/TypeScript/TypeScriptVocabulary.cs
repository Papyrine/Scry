/// <summary>
/// Renders the wire vocabulary as TypeScript, for a client that is not C#: every envelope, operator,
/// node and projection value as an interface, every closed name set as a constant list, and every
/// constant the wire declares.
/// </summary>
/// <remarks>
/// Read from the metadata <see cref="ScryJson.Options"/> writes with rather than from the CLR
/// declarations, so the names, the member order and the discriminators are the ones on the wire. A
/// member type with no spelling here throws instead of being guessed at, so a new kind of member is a
/// failing test rather than a quietly wrong declaration.
/// </remarks>
static partial class TypeScriptVocabulary
{
    static Assembly wire = typeof(QueryRequest).Assembly;

    static HashSet<Type> textual =
    [
        typeof(string),
        typeof(char),
        typeof(Guid),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(Date),
        typeof(Time),
        typeof(TimeSpan),
        // Base64, as BinaryConverter writes it.
        typeof(byte[])
    ];

    static HashSet<Type> numeric =
    [
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
        typeof(float),
        typeof(double),
        typeof(decimal)
    ];

    static HashSet<Type> lists =
    [
        typeof(IReadOnlyList<>),
        typeof(IReadOnlyCollection<>),
        typeof(IList<>),
        typeof(ICollection<>),
        typeof(List<>),
        typeof(IEnumerable<>)
    ];

    static HashSet<Type> dictionaries =
    [
        typeof(IReadOnlyDictionary<,>),
        typeof(IDictionary<,>),
        typeof(Dictionary<,>)
    ];

    public static string Render()
    {
        var vocabulary = new Vocabulary();
        foreach (var root in Roots())
        {
            vocabulary.Reach(root);
        }

        // Every closed name set, including any no envelope names yet, since a client builds requests
        // from them.
        foreach (var type in wire.GetTypes())
        {
            if (type is {IsPublic: true, IsEnum: true})
            {
                vocabulary.Reach(type);
            }
        }

        return vocabulary.Write();
    }

    // The roots WireJsonContext generates metadata from, which reach every other wire type, and the
    // page envelope, which is generic over the caller's row and so is not among them.
    static IEnumerable<Type> Roots()
    {
        var context = wire.GetType("WireJsonContext", throwOnError: true)!;
        foreach (var attribute in context.GetCustomAttributesData())
        {
            if (attribute.AttributeType == typeof(JsonSerializableAttribute))
            {
                yield return (Type) attribute.ConstructorArguments[0].Value!;
            }
        }

        yield return typeof(ScryPage<Row>);
    }

    // Stands for the type parameter of a generic envelope: the row, which is the consumer's.
    sealed class Row;

    sealed class Vocabulary
    {
        HashSet<Type> reached = [];
        Dictionary<Type, (string Property, string Value)> discriminators = Discriminators();
        SortedDictionary<string, string> declarations = new(StringComparer.Ordinal);
        SortedDictionary<string, Type> enums = new(StringComparer.Ordinal);
        NullabilityInfoContext nullability = new();
        bool path;
        bool projectionEntry;

        // Taken before anything is reached, so a case reached through a property of its own type
        // before its base is still known to be one.
        static Dictionary<Type, (string Property, string Value)> Discriminators()
        {
            var found = new Dictionary<Type, (string Property, string Value)>();
            foreach (var type in wire.GetTypes())
            {
                if (type.GetCustomAttribute<JsonPolymorphicAttribute>() is not { } polymorphic)
                {
                    continue;
                }

                foreach (var derived in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
                {
                    found.Add(derived.DerivedType, (polymorphic.TypeDiscriminatorPropertyName!, (string) derived.TypeDiscriminator!));
                }
            }

            return found;
        }

        public void Reach(Type type)
        {
            if (!reached.Add(type))
            {
                return;
            }

            if (type.IsEnum)
            {
                enums.Add(type.Name, type);
                return;
            }

            var info = ScryJson.Options.GetTypeInfo(type);
            if (info.PolymorphismOptions is { } polymorphism)
            {
                Union(type, polymorphism);
                return;
            }

            Interface(type, info);
        }

        void Union(Type type, JsonPolymorphismOptions polymorphism)
        {
            var cases = polymorphism.DerivedTypes.ToList();
            var builder = new StringBuilder();
            builder.Append($"export type {type.Name} =\n");
            foreach (var @case in cases)
            {
                builder.Append($"  | {@case.DerivedType.Name}\n");
            }

            builder.Length--;
            builder.Append(";\n");
            builder.Append($"export const {type.Name}Types = [\n");
            foreach (var @case in cases)
            {
                builder.Append($"  {Literal((string) @case.TypeDiscriminator!)},\n");
            }

            builder.Append("] as const;\n");
            declarations.Add(type.Name, builder.ToString());

            foreach (var @case in cases)
            {
                Reach(@case.DerivedType);
            }
        }

        void Interface(Type type, JsonTypeInfo info)
        {
            var builder = new StringBuilder();
            builder.Append($"export interface {Declared(type)} {{\n");
            if (discriminators.TryGetValue(type, out var discriminator))
            {
                builder.Append($"  {Key(discriminator.Property)}: {Literal(discriminator.Value)};\n");
            }

            foreach (var property in info.Properties.OrderBy(_ => _.Order))
            {
                // An ignored member is still listed, with nothing to read it through: it never
                // travels.
                if (property.Get is null)
                {
                    continue;
                }

                var spelling = Spell(type, property);
                var optional = "";
                if (Omittable(type, property))
                {
                    optional = "?";
                }

                builder.Append($"  {Key(property.Name)}{optional}: {spelling};\n");
            }

            builder.Append("}\n");
            declarations.Add(type.Name.Split('`')[0], builder.ToString());
        }

        // A member is left out of what the server writes when it holds null, every null being omitted,
        // or when its own attribute leaves out its default.
        bool Omittable(Type type, JsonPropertyInfo property)
        {
            if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            {
                return true;
            }

            var member = Member(type, property);
            if (member.GetCustomAttribute<JsonIgnoreAttribute>() is {Condition: JsonIgnoreCondition.WhenWritingDefault})
            {
                return true;
            }

            if (property.PropertyType.IsValueType)
            {
                return false;
            }

            if (member is System.Reflection.PropertyInfo info)
            {
                return nullability.Create(info).ReadState == NullabilityState.Nullable;
            }

            return nullability.Create((FieldInfo) member).ReadState == NullabilityState.Nullable;
        }

        static MemberInfo Member(Type type, JsonPropertyInfo property)
        {
            if (property.AttributeProvider is MemberInfo member)
            {
                return member;
            }

            throw new($"The metadata for '{type.Name}.{property.Name}' carries no member to read its nullability from.");
        }

        // The members written by a converter of their own, rather than as their type would be.
        string Spell(Type type, JsonPropertyInfo property)
        {
            var converter = property.CustomConverter?.GetType().Name;
            if (converter is null)
            {
                return Spell(property.PropertyType, $"{type.Name}.{property.Name}");
            }

            if (converter == "PathConverter")
            {
                path = true;
                return "Path";
            }

            if (converter == "ProjectionMembersConverter")
            {
                projectionEntry = true;
                Reach(typeof(ProjectionMember));
                return "ProjectionEntry[]";
            }

            if (converter == "PayloadConverter")
            {
                return "unknown";
            }

            throw new($"'{type.Name}.{property.Name}' is written by {converter}, which has no TypeScript spelling here. Add one beside the others, as that converter writes it.");
        }

        // Where is the member being spelled, for the refusal to name.
        string Spell(Type type, string where)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(Row))
            {
                return "T";
            }

            if (underlying == typeof(bool))
            {
                return "boolean";
            }

            if (underlying == typeof(JsonElement))
            {
                return "unknown";
            }

            if (textual.Contains(underlying))
            {
                return "string";
            }

            if (numeric.Contains(underlying))
            {
                return "number";
            }

            if (underlying.IsEnum)
            {
                Reach(underlying);
                return underlying.Name;
            }

            if (underlying.IsArray)
            {
                return $"{Spell(underlying.GetElementType()!, where)}[]";
            }

            if (underlying.IsGenericType &&
                !Wire(underlying))
            {
                var definition = underlying.GetGenericTypeDefinition();
                var arguments = underlying.GetGenericArguments();
                if (lists.Contains(definition))
                {
                    return $"{Spell(arguments[0], where)}[]";
                }

                if (dictionaries.Contains(definition) &&
                    arguments[0] == typeof(string))
                {
                    return $"Record<string, {Spell(arguments[1], where)}>";
                }
            }

            if (Wire(underlying) &&
                underlying.IsClass)
            {
                Reach(underlying);
                // A case named as itself rather than through its base is written without the
                // discriminator, which only the base's polymorphism adds.
                if (discriminators.TryGetValue(underlying, out var discriminator))
                {
                    return $"Omit<{underlying.Name}, {Literal(discriminator.Property)}>";
                }

                return Referenced(underlying, where);
            }

            throw new($"'{where}' is a '{type}', which has no TypeScript spelling here. Add one, as the wire writes it.");
        }

        static bool Wire(Type type) =>
            type.Assembly == wire;

        static string Declared(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }

            var parameters = type.GetGenericTypeDefinition().GetGenericArguments().Select(_ => _.Name);
            return $"{type.Name.Split('`')[0]}<{string.Join(", ", parameters)}>";
        }

        string Referenced(Type type, string where)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }

            var arguments = type.GetGenericArguments().Select(_ => Spell(_, where));
            return $"{type.Name.Split('`')[0]}<{string.Join(", ", arguments)}>";
        }

        public string Write()
        {
            var builder = new StringBuilder(
                """
                // The Scry wire vocabulary, as ScryJson writes it. Generated by TypeScriptFixtureTests from
                // the types in Scry.Wire: do not edit. A change to the vocabulary fails that test, and
                // accepting its new snapshot is the edit.
                //
                // Every interface lists its members in the order the server writes them. A request is one
                // byte-string only when it is written in that order too, and the cache, the URL form and
                // the audit all key off those bytes (docs/wire-format.md, "One spelling per request"). A
                // member marked optional is omitted when absent: never written as null.


                """);
            builder.Append("// Constants.\n\n");
            foreach (var type in wire.GetTypes()
                         .Where(_ => _ is {IsPublic: true, IsEnum: false})
                         .OrderBy(_ => _.Name, StringComparer.Ordinal))
            {
                Constants(builder, type);
            }

            builder.Append("// Closed name sets. Each travels by name, matched case-sensitively.\n\n");
            foreach (var (name, type) in enums)
            {
                builder.Append($"export const {name} = [\n");
                foreach (var value in Enum.GetNames(type))
                {
                    builder.Append($"  {Literal(value)},\n");
                }

                builder.Append("] as const;\n");
                builder.Append($"export type {name} = (typeof {name})[number];\n\n");
            }

            builder.Append("// Shapes.\n\n");
            if (path)
            {
                builder.Append(
                    """
                    /**
                     * A member path. One segment travels as a bare string and any other number as an
                     * array; a one-element array is refused, so each path has a single spelling.
                     */
                    export type Path = string | string[];


                    """);
            }

            if (projectionEntry)
            {
                builder.Append(
                    """
                    /**
                     * A projection member. One reading the member it is named for (a node over that
                     * single-segment path) travels as its bare name; every other as an object. The object
                     * spelling of a member that qualifies for the name is refused.
                     */
                    export type ProjectionEntry = string | ProjectionMember;


                    """);
            }

            foreach (var declaration in declarations.Values)
            {
                builder.Append(declaration);
                builder.Append('\n');
            }

            builder.Length--;
            return builder.ToString();
        }

        static void Constants(StringBuilder builder, Type type)
        {
            var fields = type
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(_ => _.IsLiteral)
                .OrderBy(_ => _.MetadataToken)
                .ToList();
            if (fields.Count == 0)
            {
                return;
            }

            builder.Append($"export const {type.Name} = {{\n");
            foreach (var field in fields)
            {
                builder.Append($"  {field.Name}: {Value(field.GetRawConstantValue())},\n");
            }

            builder.Append("} as const;\n\n");
        }

        static string Value(object? value)
        {
            if (value is string text)
            {
                return Literal(text);
            }

            if (value is int or long or short or byte)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            }

            throw new($"A wire constant of type '{value?.GetType()}' has no TypeScript spelling here.");
        }

        static string Key(string name)
        {
            if (Identifier().IsMatch(name))
            {
                return name;
            }

            return Literal(name);
        }

        static string Literal(string value) =>
            JsonSerializer.Serialize(value);
    }

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex Identifier();
}
