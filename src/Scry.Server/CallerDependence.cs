/// <summary>
/// A source whose rows may depend on who asked, why, and — where there is one — the registration
/// that would not. What the startup guard against an unscoped cache names.
/// </summary>
readonly record struct CallerDependence(string Source, string Why, string? Hint);
