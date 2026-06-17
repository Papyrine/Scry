namespace Pneumatic;

/// <summary>
/// Thrown when an incoming query violates the allow-list or a resource limit. The executor fails
/// closed: the query is rejected before any expression is rebound or executed.
/// </summary>
public sealed class PneumaticValidationException(string message) :
    Exception(message);
