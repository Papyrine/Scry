/// <summary>
/// Writes a list result straight from projected <c>object[]</c> rows to UTF-8 — no per-row
/// dictionaries, no <see cref="JsonElement"/> round trip, no reflection walk over the envelope.
/// Byte-for-byte identical to serializing the equivalent <see cref="QueryResponse"/>: that identity
/// is pinned by the integration golden tests, and any case this writer cannot reproduce exactly
/// stays on the general path instead.
/// </summary>
static class ResponseWriter
{
    static readonly JsonEncodedText version = JsonEncodedText.Encode("version");
    static readonly JsonEncodedText kind = JsonEncodedText.Encode("kind");
    static readonly JsonEncodedText payload = JsonEncodedText.Encode("payload");
    static readonly JsonEncodedText stamp = JsonEncodedText.Encode("stamp");
    static readonly JsonEncodedText list = JsonEncodedText.Encode(nameof(ResultKind.List));

    /// <summary>Writes the whole list envelope — version, kind, rows, stamp — returning the row count.</summary>
    public static int WriteList(IBufferWriter<byte> output, QueryExecutor.RowSet set, string schemaStamp)
    {
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteNumber(version, WireFormat.Version);
        json.WriteString(kind, list);
        json.WritePropertyName(payload);
        json.WriteStartArray();

        var writer = set.Plan.Writer;
        var rows = 0;
        foreach (var row in set.Rows)
        {
            writer.WriteRow(json, Row(row!, set));
            rows++;
        }

        json.WriteEndArray();
        json.WriteString(stamp, schemaStamp);
        json.WriteEndObject();
        json.Flush();
        return rows;
    }

    public static object[] Row(object row, QueryExecutor.RowSet set) =>
        set.Deduplicated
            ? ExpressionBuilder.ReadDistinctRow(row, set.Plan.Shape.Count)
            : (object[])row;
}

/// <summary>
/// The per-shape row writer: the projection's slot paths merged into one name tree, with every name
/// camel-cased and JSON-escaped once, so writing a row is a walk over precomputed
/// <see cref="JsonEncodedText"/> and boxed values.
/// </summary>
/// <remarks>
/// The tree reproduces the exact semantics of the dictionary shaping it replaces — built on original
/// (pre-camel) names, a duplicate leaf overwrites in place, and a nested path claims the position of
/// any scalar it collides with — so a pathological projection serializes identically on both paths.
/// </remarks>
sealed class PlanShapeWriter
{
    sealed class Node(string name)
    {
        public JsonEncodedText Encoded = JsonEncodedText.Encode(JsonNamingPolicy.CamelCase.ConvertName(name));
        public int Slot = -1;
        public List<Node>? Children;
        public Dictionary<string, int>? Index;
    }

    readonly Node root;

    PlanShapeWriter(Node root) =>
        this.root = root;

    public static PlanShapeWriter Create(IReadOnlyList<IReadOnlyList<string>> shape)
    {
        var root = Branch(new(""));
        for (var slot = 0; slot < shape.Count; slot++)
        {
            var path = shape[slot];
            var node = root;
            for (var segment = 0; segment < path.Count - 1; segment++)
            {
                var child = Child(node, path[segment]);
                if (child.Children is null)
                {
                    // A scalar leaf under this name gives way to the nested object, keeping its
                    // position — the same replacement the dictionary path performs.
                    child.Slot = -1;
                    Branch(child);
                }

                node = child;
            }

            var leaf = Child(node, path[^1]);
            // A repeated name overwrites in place; a leaf claiming an object's name drops its children.
            leaf.Children = null;
            leaf.Index = null;
            leaf.Slot = slot;
        }

        return new(root);
    }

    static Node Branch(Node node)
    {
        node.Children = [];
        node.Index = new(StringComparer.Ordinal);
        return node;
    }

    static Node Child(Node parent, string name)
    {
        if (parent.Index!.TryGetValue(name, out var position))
        {
            return parent.Children![position];
        }

        var child = new Node(name);
        parent.Index[name] = parent.Children!.Count;
        parent.Children.Add(child);
        return child;
    }

    public void WriteRow(Utf8JsonWriter json, object[] row) =>
        WriteObject(json, root, row);

    static void WriteObject(Utf8JsonWriter json, Node node, object[] row)
    {
        json.WriteStartObject();
        foreach (var child in node.Children!)
        {
            json.WritePropertyName(child.Encoded);
            if (child.Children is null)
            {
                WriteValue(json, row[child.Slot]);
            }
            else
            {
                WriteObject(json, child, row);
            }
        }

        json.WriteEndObject();
    }

    // The fast cases call the same Utf8JsonWriter primitives the serializer's own converters call,
    // so their bytes are identical by construction. Everything else — enums (name-writing tolerant
    // converter), dates-without-time, chars, unsigned widths — goes through the serializer itself.
    static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case int number:
                json.WriteNumberValue(number);
                break;
            case long number:
                json.WriteNumberValue(number);
                break;
            case decimal number:
                json.WriteNumberValue(number);
                break;
            case double number:
                json.WriteNumberValue(number);
                break;
            case DateTime moment:
                json.WriteStringValue(moment);
                break;
            case Guid guid:
                json.WriteStringValue(guid);
                break;
            default:
                JsonSerializer.Serialize(json, value, value.GetType(), ScryJson.Options);
                break;
        }
    }
}
