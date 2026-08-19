namespace Sample.Model;

// begin-snippet: queryableOrder
[Queryable]
public class Order
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public decimal Amount { get; set; }

    // A collection of values, which EF stores as a JSON column. Present in the sample so the round-trip
    // tests cover one end to end: the generator spells its element from the model DLL and the server
    // spells it from reflection, and the two stamps only agree if they agree about this member.
    [QueryableCollection]
    public List<string> Tags { get; set; } = [];

    // What the cached row policy on this type reads to know a row needs deciding again. Server-side
    // machinery rather than query surface, so it is hidden from clients like anything else Scry was
    // not told to expose — a version column need not be one a client can see. A real deployment more
    // often maps a rowversion as ulong and lets the database move it; this one writes it, so the
    // sample can show a row being re-decided on demand.
    [QueryIgnore]
    public long Revision { get; set; }
}
// end-snippet