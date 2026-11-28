using System.Data.Common;
using System.Text.Encodings.Web;

/// <summary>
/// What a TypeScript client is built and tested against, written beside this file so that a change on
/// this side reaches that client as a failing test here first: the wire vocabulary as declarations,
/// the shape corpus as exact request and response bytes, the introspection a client is generated
/// from, and the casing every row's names are written in.
/// </summary>
/// <remarks>
/// Exact bytes rather than structure. The cache, the URL form and the audit all key off a request's
/// bytes, so a client whose request means the same thing but is spelled differently is, to every one
/// of them, a different request.
/// </remarks>
public partial class TypeScriptFixtureTests
{
    [Test]
    public Task Wire() =>
        Verify(TypeScriptVocabulary.Render(), "mts")
            .UseFileName("wire")
            .NotInline();

    // The shared processor with the collations a corpus shape compares under: a server without them
    // refuses those shapes, and the corpus is here to show what one answers.
    static ScryProcessor processor = ScryProcessor.Create<TestContext>(
        options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.CaseSensitiveCollation = "Latin1_General_CS_AS";
            options.CaseInsensitiveCollation = "Latin1_General_CI_AS";
        });

    /// <summary>
    /// Every request in the <see cref="WireShapeTests"/> corpus, compact as it travels, and what the
    /// server made of it over the seeded test database. Request and response are JSON text held in a
    /// string, so that reading the fixture hands back the bytes themselves rather than a value they
    /// parse to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shape is written for the request it produces, and not every one is a query this server
    /// answers: the translator lets through what only the server can refuse, and some shapes fault
    /// on the seeded rows. A refusal carries the message a <c>400</c> would, under <c>refused</c>,
    /// and a fault only <c>failed</c>, since a <c>500</c> answers every fault with the same message.
    /// </para>
    /// <para>
    /// The response's schema stamp is left out: it changes with every change to the test model, which
    /// would rewrite every answer here for nothing any of them says. The stamp's own spelling is in
    /// the introspection fixture. A page's cursor is written as <c>{cursor}</c>, being sealed under
    /// a key each processor makes for itself, and opaque to a client in any case.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Requests()
    {
        await using var context = TestContext.CreateSeeded();
        var groups = await WireShapeTests.Groups();

        var json = Document(writer =>
        {
            writer.WriteStartArray();
            foreach (var (group, entries) in groups)
            {
                foreach (var entry in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("group", group);
                    writer.WriteString("csharp", entry.Query);
                    writer.WriteString("request", ScryJson.Serialize(entry.Request));
                    Answer(writer, entry.Request, context);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        });

        await Verify(json, "json")
            .UseFileName("requests")
            .NotInline();
    }

    /// <summary>
    /// Every constant tag appears in some request of the corpus, so a client matching it has spelled
    /// a constant of each shape at least once.
    /// </summary>
    [Test]
    public async Task EveryConstantTagIsInTheCorpus()
    {
        var json = string.Concat((await WireShapeTests.Groups()).SelectMany(_ => _.Entries).Select(_ => ScryJson.Serialize(_.Request)));
        var used = Tag()
            .Matches(json)
            .Select(_ => _.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = Enum.GetNames<ClrTypeTag>()
            .Where(_ => !used.Contains(_))
            .ToList();

        await Assert.That(missing).IsEmpty().Because($"No request in the corpus carries a constant tagged {string.Join(", ", missing)}. Add a shape to WireShapeTests that does.");
    }

    /// <summary>
    /// The description of the test model, which is what the corpus above was written against: a
    /// client generated from this document writes the same requests.
    /// </summary>
    [Test]
    public Task Introspection() =>
        Verify(JsonSerializer.Serialize(SharedProcessor.Instance.Describe(), indented), "json")
            .UseFileName("introspection")
            .NotInline();

    /// <summary>
    /// The enums of the base class library a member may be typed as, described as introspection
    /// describes a model's own. Introspection names one only by its display, since a C# client
    /// already has it; a client in another language has to be told its values.
    /// </summary>
    /// <remarks>
    /// A module rather than JSON, as the wire vocabulary is: Verify writes a byte-order mark, which a
    /// JavaScript parser skips and a JSON one refuses, and a bundler reading the TypeScript generator
    /// (Vite, loading a configuration that imports its plugin) parses what the generator imports.
    /// </remarks>
    [Test]
    public Task BclEnums()
    {
        var table = typeof(ScryProcessor).Assembly.GetType("BclEnums", throwOnError: true)!;
        var types = (Type[]) table.GetField("types", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var json = Document(writer =>
        {
            writer.WriteStartArray();
            foreach (var type in types)
            {
                writer.WriteStartObject();
                writer.WriteString("display", $"global::{type.FullName}");
                writer.WriteString("name", type.Name);
                writer.WriteStartArray("values");
                foreach (var name in Enum.GetNames(type))
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("constants");
                foreach (var value in Enum.GetValuesAsUnderlyingType(type))
                {
                    writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                }

                writer.WriteEndArray();
                writer.WriteBoolean("isFlags", type.IsDefined(typeof(FlagsAttribute), inherit: false));
                writer.WriteString("underlying", Underlying(type));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });

        var module = $"""
            // The base class library enums a member may be typed as, which introspection names by
            // display alone. Generated by TypeScriptFixtureTests from the server's own table: do not edit.

            export const bclEnums = {json};

            """;
        return Verify(module, "mts")
            .UseFileName("bcl-enums")
            .NotInline();
    }

    // As introspection spells an enum's underlying type. Every listed enum is an int today; another
    // would need its keyword here.
    static string Underlying(Type type)
    {
        if (Enum.GetUnderlyingType(type) == typeof(int))
        {
            return "int";
        }

        throw new($"'{type.Name}' is backed by '{Enum.GetUnderlyingType(type)}', which has no keyword here.");
    }

    /// <summary>
    /// What <see cref="JsonNamingPolicy.CamelCase"/> makes of a name, which is how every row writes its
    /// members' names. A projection is sent under the names it was written with, and a client reading
    /// the rows back has to arrive at the same keys: every member name in the test model, and the
    /// cases where the policy departs from lowering the first letter.
    /// </summary>
    [Test]
    public Task Names()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal)
        {
            // A run of capitals is lowered up to the one starting the next word.
            "ID",
            "URL",
            "URLValue",
            "IOStream",
            "HTML5Parser",
            "ABc",
            "AB",
            "A",
            // Already camel-cased, or starting with something that is not a capital.
            "name",
            "_Name",
            "9Lives",
            // A capital outside ASCII is lowered too, one char at a time and invariantly: which
            // leaves 'İ' as it is, where a language's own lowering makes it an 'i' and a combining
            // dot.
            "ÉtéRow",
            "ΣΑΣ",
            "İstanbul",
            // Neither is a capital to the policy: a titlecase letter, and a letter outside the
            // basic plane, read one surrogate at a time.
            "ǅx",
            "𝐀bc",
            // A space ends the run.
            "AB C"
        };
        foreach (var type in SharedProcessor.Instance.Describe().Types)
        {
            foreach (var member in type.Members)
            {
                names.Add(member.Name);
            }
        }

        var json = Document(writer =>
        {
            writer.WriteStartObject();
            foreach (var name in names)
            {
                writer.WriteString(name, JsonNamingPolicy.CamelCase.ConvertName(name));
            }

            writer.WriteEndObject();
        });

        return Verify(json, "json")
            .UseFileName("names")
            .NotInline();
    }

    static void Answer(Utf8JsonWriter writer, QueryRequest request, TestContext context)
    {
        QueryResponse response;
        try
        {
            response = processor.Execute(request, context);
        }
        catch (Exception exception) when (Unwrap(exception) is ScryValidationException refusal)
        {
            writer.WriteString("refused", refusal.Message);
            return;
        }
        catch (Exception exception) when (Unwrap(exception) is InvalidOperationException or DbException)
        {
            writer.WriteBoolean("failed", true);
            return;
        }

        var text = ScryJson.Serialize(
            response with
            {
                Stamp = null
            });
        writer.WriteString("response", Cursor().Replace(text, "$1{cursor}$2"));
    }

    // A terminal whose result type is the caller's is executed through reflection, which wraps what it
    // throws.
    static Exception Unwrap(Exception exception)
    {
        if (exception is TargetInvocationException {InnerException: { } inner})
        {
            return inner;
        }

        return exception;
    }

    static JsonSerializerOptions indented = new(ScryJson.Options)
    {
        WriteIndented = true
    };

    // Indented for review, and escaped no more than JSON needs, so the wire text held in a string reads
    // as itself: a quote as \" rather than ", and every escape the wire wrote kept as written.
    static string Document(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new()
                   {
                       Indented = true,
                       NewLine = "\n",
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [GeneratedRegex("\"tag\":\"([^\"]+)\"")]
    private static partial Regex Tag();

    [GeneratedRegex("(\"cursor\":\")[^\"]*(\")")]
    private static partial Regex Cursor();
}
