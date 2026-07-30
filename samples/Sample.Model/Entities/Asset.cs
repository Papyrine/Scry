namespace Sample.Model;

/// <summary>
/// The root of a table-per-hierarchy inheritance chain. Opting the base in exposes its own members;
/// a derived type has to opt in on its own before a query can narrow to it with <c>OfType</c> or
/// read the members it declares.
/// </summary>
[Queryable]
public class Asset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

[Queryable]
public class Vehicle : Asset
{
    public int Wheels { get; set; }
}

[Queryable]
public class Building : Asset
{
    public int Floors { get; set; }
}
