# Skry.Client

Client-side LINQ provider for [Skry](https://github.com/Papyrine/Skry). Write ordinary LINQ
against the source-generated query models; Skry captures it, serializes it to the query AST, and
sends it to the server. No EF dependency, so it stays small in a trimmed Blazor WebAssembly app.

```csharp
var rows = await Query.Employees
    .Where(e => e.Status == Status.FullTime && e.Name.StartsWith("A"))
    .OrderBy(e => e.Name)
    .Select(e => new Row(e.Name, e.Status, e.Manager!.Name))
    .ToSkryListAsync();
```
