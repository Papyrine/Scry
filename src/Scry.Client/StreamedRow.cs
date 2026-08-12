/// <summary>
/// One row of a streamed result, together with everything needed to read it: the enum aliases the
/// stream opened with and the binary parts that belong to this row and no other.
/// </summary>
/// <remarks>
/// <para>
/// Carrying them on the row is what lets two streams run at once on one <see cref="ScryClient"/>. They
/// used to sit on the client and be set just before each row was yielded, so a second enumeration
/// overwrote the first's between its yield and its read — and a client is registered per scope, which
/// for a WASM app is the whole app.
/// </para>
/// <para>
/// A row arrives either as the UTF-8 line it was read from — the HTTP transport, which has the bytes
/// and no reason to build a document out of them — or as a <see cref="JsonElement"/>, which is the
/// shape a transport supplied to <see cref="ScryClient(Func{QueryRequest, Cancel, Task{QueryResponse}}, Func{QueryRequest, Cancel, IAsyncEnumerable{JsonElement}}?, Func{QueryBatchRequest, Cancel, Task{QueryBatchResponse}}?)"/>
/// produces. <see cref="Utf8"/> is only valid until the next row is pulled.
/// </para>
/// </remarks>
readonly struct StreamedRow
{
    readonly ReadOnlyMemory<byte> utf8;
    readonly JsonElement element;
    readonly bool fromBytes;

    StreamedRow(ReadOnlyMemory<byte> utf8, JsonElement element, bool fromBytes)
    {
        this.utf8 = utf8;
        this.element = element;
        this.fromBytes = fromBytes;
    }

    public static StreamedRow FromUtf8(ReadOnlyMemory<byte> utf8) =>
        new(utf8, default, fromBytes: true);

    public static StreamedRow FromElement(JsonElement element) =>
        new(default, element, fromBytes: false);

    public ReadOnlyMemory<byte> Utf8 => utf8;

    /// <summary>The enum aliases the stream's opening marker carried, if any.</summary>
    public IReadOnlyList<EnumAlias>? Aliases { get; init; }

    /// <summary>The binary parts this row's placeholders reference, if any.</summary>
    public IReadOnlyList<byte[]>? Parts { get; init; }

    /// <summary>Reads the row into the client's generated model.</summary>
    public T? Materialize<T>() =>
        fromBytes
            ? ScryJson.DeserializeRow<T>(utf8.Span, Aliases, Parts)
            : ScryJson.DeserializeRow<T>(element, Aliases, Parts);
}
