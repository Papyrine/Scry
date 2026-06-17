namespace Skry.Wire;

/// <summary>
/// Thrown when a query request or response cannot be parsed. The (de)serializer fails closed:
/// unknown type discriminators and malformed payloads never produce a partial query.
/// </summary>
public sealed class SkryWireException :
    Exception
{
    public SkryWireException(string message) :
        base(message)
    {
    }

    public SkryWireException(string message, Exception inner) :
        base(message, inner)
    {
    }
}
