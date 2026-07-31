namespace Scry;

// begin-snippet: wireSubqueryFunctions
/// <summary>
/// The questions a client may ask about an exposed collection navigation. Every one folds the
/// collection to a single value — there is no function here that returns its rows.
/// </summary>
public enum SubqueryFn
{
    Any,
    All,
    Count,
    Sum,
    Average,
    Min,
    Max
}
// end-snippet
