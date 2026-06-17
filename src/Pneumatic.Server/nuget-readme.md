# Pneumatic.Server

Server-side execution for [Pneumatic](https://github.com/Papyrine/Pneumatic). Validates an incoming
query AST against the allow-list, rebinds it to the real EF Core entity types, applies row-level
policies, executes against a `DbContext`, and returns projected rows.

```csharp
builder.Services.AddPneumatic(options =>
{
    options.UseModel<SampleContext>();
    options.AddPocoSource<Holiday>(_ => Holiday.Seed());
    options.MaxPageSize = 200;
    options.AddPolicy<Employee, EmployeePolicy>();
});

app.MapPneumatic("/api/query");
```
