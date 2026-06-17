namespace Pneumatic.Wire;

/// <summary>
/// Thrown when a query request or response cannot be parsed. The (de)serializer fails closed:
/// unknown type discriminators and malformed payloads never produce a partial query.
/// </summary>
public sealed class PneumaticWireException :
    Exception
{
    public PneumaticWireException(string message) :
        base(message)
    {
    }

    public PneumaticWireException(string message, Exception inner) :
        base(message, inner)
    {
    }
}
