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
}
// end-snippet