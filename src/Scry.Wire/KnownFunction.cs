namespace Scry.Wire;

// begin-snippet: wireFunctions
/// <summary>The closed set of functions a client may call on a value. No free-form method names.</summary>
public enum KnownFunction
{
    StringContains,
    StringStartsWith,
    StringEndsWith,
    StringToLower,
    StringToUpper,
    StringIsNullOrEmpty,
    DateYear,
    DateMonth,
    DateDay
}
// end-snippet
