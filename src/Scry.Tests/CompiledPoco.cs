using Microsoft.EntityFrameworkCore;

/// <summary>
/// A processor over a source compiled for one test and served in memory: a shape the shared model has
/// no member of, queried under LINQ to Objects. The context is never opened — every query reads the
/// poco source — so the connection it is given names nothing that exists.
/// </summary>
static class CompiledPoco
{
    /// <summary>
    /// Compiles <paramref name="model"/> — a <c>[QueryablePoco]</c> class named <paramref name="source"/>,
    /// whose static <c>Rows()</c> is its rows — beside a context of its own.
    /// </summary>
    public static (ScryProcessor Processor, DbContext Data) Start(string model, string source)
    {
        var (assembly, _) = CompiledModel.Compile(
            $"""
             public sealed class PocoContext(DbContextOptions options) : DbContext(options);

             {model}
             """);
        var contextType = assembly.GetType("PocoContext")!;
        var sourceType = assembly.GetType(source)!;
        var rows = (IEnumerable) sourceType.GetMethod("Rows")!.Invoke(null, null)!;

        var options = new ScryOptions(contextType)
        {
            PocoSources =
            {
                [sourceType] = _ => rows.AsQueryable()
            }
        };
        var processor = new ScryProcessor(Schema.Build(options), options);

        var contextOptions = SqlServerDbContextOptionsExtensions
            .UseSqlServer(new DbContextOptionsBuilder(), "Server=(localdb)\\ScryUnused;Database=ScryUnused")
            .Options;
        var data = (DbContext) Activator.CreateInstance(contextType, contextOptions)!;
        return (processor, data);
    }
}
