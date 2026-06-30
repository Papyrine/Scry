public class Employee
{
    public string Name { get; set; } = "";
    public Status Status { get; set; }
    public bool Active { get; set; }
    public Employee? Manager { get; set; }
    public Department? Department { get; set; }
}