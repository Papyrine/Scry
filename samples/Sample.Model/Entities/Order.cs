namespace Sample.Model;

// begin-snippet: queryableOrder
[Queryable]
public class Order
{
    public int Id { get; set; }
    public string Region { get; set; } = "";
    public decimal Amount { get; set; }
}
// end-snippet