namespace Scry.Wire;

// begin-snippet: wireVersion
/// <summary>Wire format version constants.</summary>
public static class WireFormat
{
    /// <summary>The current wire format version.</summary>
    public const int Version = 1;
}
// end-snippet

// begin-snippet: wireBinaryOps
/// <summary>Binary operators allowed in a predicate or projection expression.</summary>
public enum BinaryOp
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    AndAlso,
    OrElse,
    Add,
    Subtract,
    Multiply,
    Divide
}

/// <summary>Unary operators allowed in an expression.</summary>
public enum UnaryOp
{
    Not,
    Negate
}
// end-snippet

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

// begin-snippet: wireAggregates
/// <summary>Aggregate functions allowed in a projection over a grouped query.</summary>
public enum AggregateFn
{
    Count,
    Sum,
    Average,
    Min,
    Max
}
// end-snippet

// begin-snippet: wireTypeTags
/// <summary>The CLR shape of a constant literal on the wire. The server reconciles it against the
/// member type at the comparison site.</summary>
public enum ClrTypeTag
{
    Null,
    String,
    Boolean,
    Int32,
    Int64,
    Decimal,
    Double,
    DateTime,
    DateOnly,
    Guid,
    Bytes,
    Enum
}
// end-snippet

/// <summary>The shape of a query result.</summary>
public enum ResultKind
{
    List,
    Scalar,
    Single,
    Page
}
