# Skry.Server

Server-side execution for [Skry](https://github.com/Papyrine/Skry). Validates an incoming
query AST against the allow-list, rebinds it to the real EF Core entity types, applies row-level
policies, executes against a `DbContext`, and returns projected rows.

```csharp
builder.Services.AddSkry(options =>
{
    options.UseModel<SampleContext>();
    options.AddPocoSource<Holiday>(_ => Holiday.Seed());
    options.MaxPageSize = 200;
    options.AddPolicy<Employee, EmployeePolicy>();
});

app.MapSkry("/api/query");
```
