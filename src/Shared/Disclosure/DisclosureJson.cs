using System.Text.Json.Serialization;

/// <summary>
/// How the explorer's questions and answers are written, generated at build time: the browser app is
/// trimmed, and a serializer that found these types by reflection would find them gone.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CatalogAnswer))]
[JsonSerializable(typeof(RowQuestion))]
[JsonSerializable(typeof(RowAnswer))]
[JsonSerializable(typeof(CallerQuestion))]
[JsonSerializable(typeof(MemberQuestion))]
[JsonSerializable(typeof(EventsAnswer))]
[JsonSerializable(typeof(EventAnswer))]
[JsonSerializable(typeof(StatusAnswer))]
[JsonSerializable(typeof(VerifyQuestion))]
[JsonSerializable(typeof(CheckLine))]
[JsonSerializable(typeof(EraseQuestion))]
[JsonSerializable(typeof(ErasureLine))]
[JsonSerializable(typeof(ExportQuestion))]
[JsonSerializable(typeof(Refusal))]
sealed partial class DisclosureJson :
    JsonSerializerContext;
