namespace Scry.Wire;

/// <summary>
/// Thrown when a query request or response cannot be parsed. The (de)serializer fails closed:
/// unknown type discriminators and malformed payloads never produce a partial query.
/// </summary>
public sealed class ScryWireException :
    Exception
{
    public ScryWireException(string message) :
        base(message)
    {
    }

    public ScryWireException(string message, Exception inner) :
        base(message, inner)
    {
    }
}
