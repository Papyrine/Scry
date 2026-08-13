/// <summary>
/// Writes a list result straight from projected <c>object[]</c> rows to UTF-8 — no per-row
/// dictionaries, no <see cref="JsonElement"/> round trip, no reflection walk over the envelope — and
/// the batch envelope that carries several of them. Byte-for-byte identical to serializing the
/// equivalent <see cref="QueryResponse"/> or <see cref="QueryBatchResponse"/>: that identity is pinned
/// by the integration golden tests, and any case this writer cannot reproduce exactly stays on the
/// general path instead.
/// </summary>
static class ResponseWriter
{
    static readonly JsonEncodedText version = JsonEncodedText.Encode("version");
    static readonly JsonEncodedText kind = JsonEncodedText.Encode("kind");
    static readonly JsonEncodedText payload = JsonEncodedText.Encode("payload");
    static readonly JsonEncodedText stamp = JsonEncodedText.Encode("stamp");
    static readonly JsonEncodedText list = JsonEncodedText.Encode(nameof(ResultKind.List));
    static readonly JsonEncodedText page = JsonEncodedText.Encode(nameof(ResultKind.Page));
    static readonly JsonEncodedText items = JsonEncodedText.Encode("items");
    static readonly JsonEncodedText hasMore = JsonEncodedText.Encode("hasMore");
    static readonly JsonEncodedText cursor = JsonEncodedText.Encode("cursor");
    static readonly JsonEncodedText results = JsonEncodedText.Encode("results");
    static readonly JsonEncodedText response = JsonEncodedText.Encode("response");
    static readonly JsonEncodedText error = JsonEncodedText.Encode("error");
    static readonly JsonEncodedText status = JsonEncodedText.Encode("status");
    static readonly JsonEncodedText staleClient = JsonEncodedText.Encode("staleClient");

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
            writer.WriteRow(json, Row(row!, set), set.Binary);
            rows++;
        }

        json.WriteEndArray();
        json.WriteString(stamp, schemaStamp);
        json.WriteEndObject();
        json.Flush();
        return rows;
    }

    /// <summary>
    /// Writes the whole page envelope — the same header a list carries, then the
    /// <see cref="ScryPage{T}"/> shape around the rows — returning the row count.
    /// </summary>
    /// <remarks>
    /// The member order and the omitted null cursor are <see cref="ScryPage{T}"/>'s own, as
    /// <c>ScryJson.Options</c> renders it: <c>items</c>, <c>hasMore</c>, then <c>cursor</c> only when
    /// there is one (<c>DefaultIgnoreCondition</c> is <c>WhenWritingNull</c>). The rows themselves go
    /// through the same shape writer a list's do, so a page and a list of the same projection are
    /// written by the same code.
    /// </remarks>
    public static int WritePage(IBufferWriter<byte> output, QueryExecutor.PageSet set, string schemaStamp)
    {
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteNumber(version, WireFormat.Version);
        json.WriteString(kind, page);
        json.WritePropertyName(payload);
        json.WriteStartObject();
        json.WritePropertyName(items);
        json.WriteStartArray();

        var writer = set.Plan.Writer;
        foreach (var row in set.Rows)
        {
            writer.WriteRow(json, row, set.Binary);
        }

        json.WriteEndArray();
        json.WriteBoolean(hasMore, set.HasMore);
        if (set.Cursor is { } resume)
        {
            json.WriteString(cursor, resume);
        }

        json.WriteEndObject();
        json.WriteString(stamp, schemaStamp);
        json.WriteEndObject();
        json.Flush();
        return set.Rows.Count;
    }

    public static object[] Row(object row, QueryExecutor.RowSet set) =>
        set.Deduplicated
            ? ExpressionBuilder.ReadDistinctRow(row, set.Plan.Shape.Count)
            : (object[])row;

    /// <summary>Opens a batch envelope: its version, then the array its entries are written into.</summary>
    public static void BeginBatch(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteNumber(version, WireFormat.Version);
        json.WritePropertyName(results);
        json.WriteStartArray();
    }

    /// <summary>
    /// Closes a batch envelope with the stamp the whole batch was answered by — carried once, because
    /// every entry was answered by the same model.
    /// </summary>
    public static void EndBatch(Utf8JsonWriter json, string schemaStamp)
    {
        json.WriteEndArray();
        json.WriteString(stamp, schemaStamp);
        json.WriteEndObject();
    }

    /// <summary>
    /// An entry already written by <see cref="WriteList"/> or <see cref="WritePage"/>. Those produce a
    /// complete response envelope, which is exactly what the entry's <c>response</c> is — so it is
    /// inserted as it stands rather than parsed back into a document to be written out again.
    /// </summary>
    public static void WriteEntry(Utf8JsonWriter json, ReadOnlySpan<byte> written)
    {
        json.WriteStartObject();
        json.WritePropertyName(response);
        // Written by this file a moment ago, so there is nothing for validation to find.
        json.WriteRawValue(written, skipInputValidation: true);
        json.WriteEndObject();
    }

    /// <summary>
    /// An entry the row writer could not produce — a terminal result, or the alias-carrying envelope a
    /// drifted client is answered with — serialized into the envelope being written.
    /// </summary>
    public static void WriteEntry(Utf8JsonWriter json, QueryResponse fallback)
    {
        json.WriteStartObject();
        json.WritePropertyName(response);
        ScryJson.Write(json, fallback);
        json.WriteEndObject();
    }

    /// <summary>
    /// An entry that was rejected or failed. Mirrors what <see cref="QueryBatchResult"/> serializes to:
    /// no <c>response</c>, and a <c>staleClient</c> written only when it is true, since the member is
    /// omitted when it is its default.
    /// </summary>
    public static void WriteEntry(Utf8JsonWriter json, string message, int entryStatus, bool stale)
    {
        json.WriteStartObject();
        json.WriteString(error, message);
        // Always 400 or 500 for a reported entry, so never the default that would omit it.
        json.WriteNumber(status, entryStatus);
        if (stale)
        {
            json.WriteBoolean(staleClient, true);
        }

        json.WriteEndObject();
    }
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
        public bool Binary;
        public List<Node>? Children;
        public Dictionary<string, int>? Index;
    }

    static readonly JsonEncodedText bin = JsonEncodedText.Encode(ScryBinary.PartProperty);

    readonly Node root;

    PlanShapeWriter(Node root) =>
        this.root = root;

    public static PlanShapeWriter Create(
        IReadOnlyList<IReadOnlyList<string>> shape,
        IReadOnlyList<bool>? binarySlots = null)
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
            leaf.Binary = binarySlots?[slot] == true;
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

    public void WriteRow(Utf8JsonWriter json, object[] row, BinaryPartCollector? binary = null) =>
        WriteObject(json, root, row, binary);

    static void WriteObject(Utf8JsonWriter json, Node node, object[] row, BinaryPartCollector? binary)
    {
        json.WriteStartObject();
        foreach (var child in node.Children!)
        {
            json.WritePropertyName(child.Encoded);
            if (child.Children is null)
            {
                // A binary slot's value leaves as a part and {"$bin":n} holds its position — the same
                // bytes serializing a BinaryPlaceholder produces on the general path. Null stays
                // inline and produces no part.
                if (child.Binary &&
                    binary is not null &&
                    row[child.Slot] is byte[] bytes)
                {
                    json.WriteStartObject();
                    json.WriteNumber(bin, binary.Add(bytes));
                    json.WriteEndObject();
                }
                else
                {
                    WriteValue(json, row[child.Slot]);
                }
            }
            else
            {
                WriteObject(json, child, row, binary);
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
