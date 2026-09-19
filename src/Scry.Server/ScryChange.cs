namespace Scry;

/// <summary>
/// What a node says to the others when data it wrote may have changed what a live query answers:
/// which entities, and which node is saying so.
/// </summary>
/// <param name="Entities">
/// The entities written, each named by the root of its EF hierarchy — a derived type, an owned type
/// and a join table all name the type their rows are read through. Empty means "anything may have
/// changed", which is what a writer that cannot say more reports.
/// </param>
/// <param name="Origin">
/// The node the change was raised on. A backplane delivers to its publisher as readily as to anyone
/// else, and this is what lets a node recognise its own message and not act on it twice.
/// </param>
/// <remarks>
/// It names entities and never rows. Whoever can write to a backplane can therefore cause live queries
/// to be asked again — which costs what the throttle lets it cost — and nothing else: every answer
/// still comes from running the query through its policies.
/// </remarks>
public sealed record ScryChange(IReadOnlyList<string> Entities, Guid Origin)
{
    /// <summary>Whether this reports that anything may have changed, rather than naming what did.</summary>
    public bool Everything => Entities.Count == 0;

    /// <summary>
    /// Writes the change as text, for a backplane to carry. One format for every backplane, so an
    /// adapter chooses a transport and nothing else — and a transport that serializes for itself is
    /// handed a string, which every one of them can carry.
    /// </summary>
    public string Serialize()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(originProperty, Origin);
            writer.WriteStartArray(entitiesProperty);
            foreach (var entity in Entities)
            {
                writer.WriteStringValue(entity);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Reads what <see cref="Serialize"/> wrote, or answers false for anything else. A backplane is
    /// shared infrastructure, so a message that is not one of these is somebody else's — or a newer
    /// node's — and is dropped rather than faulted on.
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out ScryChange? change)
    {
        change = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(originProperty, out var origin) ||
                !origin.TryGetGuid(out var node) ||
                !root.TryGetProperty(entitiesProperty, out var entities) ||
                entities.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var names = new List<string>(entities.GetArrayLength());
            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.GetString() is not { } name)
                {
                    return false;
                }

                names.Add(name);
            }

            change = new(names, node);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    const string originProperty = "origin";
    const string entitiesProperty = "entities";
}
