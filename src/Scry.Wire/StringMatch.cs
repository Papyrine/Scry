namespace Scry;

// begin-snippet: wireStringMatch
/// <summary>
/// How a string comparison should treat case. The client names the intent, never a collation: a
/// collation cannot be parameterized and would reach the SQL as text, so which one implements each
/// intent is the server's to configure.
/// </summary>
public enum StringMatch
{
    CaseSensitive,
    CaseInsensitive
}
// end-snippet
