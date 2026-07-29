[TestFixture]
public class GeneratorTests
{
    [Test]
    public Task EntitiesViewPocoAndEnum()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            public enum Status { FullTime, PartTime, Contractor }

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
                public Status Status { get; set; }
                public bool Active { get; set; }
                public int? ManagerId { get; set; }
                public Employee? Manager { get; set; }
                public byte[] Avatar { get; set; } = [];
                [QueryIgnore] public decimal Salary { get; set; }
            }

            [QueryableView]
            public class EmployeeSummary
            {
                public string Department { get; set; } = "";
                public int Headcount { get; set; }
            }

            [QueryablePoco]
            public class Holiday
            {
                public string Name { get; set; } = "";
                public System.DateOnly Date { get; set; }
            }

            public class Secret
            {
                public string Token { get; set; } = "";
            }
            """;

        return VerifyGenerated(model);
    }

    [Test]
    public Task CollectionNavigation()
    {
        // Lines opts in and is emitted as an aggregable list; Tags is a collection of a type that is
        // not opted in, and Notes is a collection that never asked to be exposed. Both stay invisible.
        const string model = """
            using System.Collections.Generic;
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Order
            {
                public int Id { get; set; }
                public string Region { get; set; } = "";
                [QueryableCollection] public List<OrderLine> Lines { get; set; } = [];
                [QueryableCollection] public List<Tag> Tags { get; set; } = [];
                public List<OrderLine> Notes { get; set; } = [];
            }

            [Queryable]
            public class OrderLine
            {
                public int Id { get; set; }
                public decimal Price { get; set; }
            }

            public class Tag
            {
                public string Name { get; set; } = "";
            }
            """;

        return VerifyGenerated(model);
    }

    [Test]
    public Task NamedSources()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable(Name = "Staff")]
            public class Employee
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            [QueryableView(Name = "Headcount")]
            public class EmployeeSummary
            {
                public int Total { get; set; }
            }

            [QueryablePoco(Name = "PublicHoliday")]
            public class Holiday
            {
                public string Name { get; set; } = "";
            }

            // A blank name is treated as unset, so this stays 'Order'.
            [Queryable(Name = "  ")]
            public class Order
            {
                public decimal Amount { get; set; }
            }
            """;

        return VerifyGenerated(model);
    }

    [Test]
    public Task ComplexType()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
                // A required and an optional complex member: both resolve to the complex query model.
                public Address Address { get; set; } = new();
                public Address? SecondaryAddress { get; set; }
            }

            // A complex value type: gets its own query model and is a navigation target, but no entry
            // point on ScryQuery. [QueryIgnore] still hides a member.
            [QueryableComplex]
            public class Address
            {
                public string City { get; set; } = "";
                public string Country { get; set; } = "";
                [QueryIgnore] public string Secret { get; set; } = "";
            }
            """;

        return VerifyGenerated(model);
    }

    [Test]
    public Task DuplicateSourceNameIsReported()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable(Name = "Staff")]
            public class Employee
            {
                public int Id { get; set; }
            }

            [Queryable(Name = "Staff")]
            public class Contractor
            {
                public int Id { get; set; }
            }
            """;

        return VerifyGenerated(model);
    }

    // The generator must ignore [PreviousNames] outright. A previous name is a server-side
    // compatibility affordance; emitting one — or letting it reach the schema stamp — would put a name
    // into the client that the current surface does not have, and would stop a rename registering as
    // drift. Identical output for the annotated and unannotated model is the check.
    [Test]
    public void PreviousNamesDoNotAffectGeneratedOutput()
    {
        const string bare = """
            using Scry;

            namespace Sample.Model;

            public enum Status { FullTime, Contractor }

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
                public Status Status { get; set; }
            }
            """;

        const string annotated = """
            using Scry;

            namespace Sample.Model;

            public enum Status { FullTime, [PreviousNames("Freelancer")] Contractor }

            [Queryable]
            [PreviousNames("Staff")]
            public class Employee
            {
                public int Id { get; set; }
                [PreviousNames("FullName")] public string Name { get; set; } = "";
                public Status Status { get; set; }
            }
            """;

        Assert.That(GeneratedSources(annotated), Is.EqualTo(GeneratedSources(bare)));
    }

    static List<string> GeneratedSources(string modelSource) =>
        RunGenerator(modelSource)
            .GetRunResult()
            .Results
            .SelectMany(_ => _.GeneratedSources)
            .Select(_ => _.SourceText.ToString())
            .ToList();

    static Task VerifyGenerated(string modelSource) =>
        Verify(RunGenerator(modelSource));

    static GeneratorDriver RunGenerator(string modelSource)
    {
        var references = ReferenceAssemblies();
        var modelCompilation = CSharpCompilation.Create(
            "Sample.Model",
            [CSharpSyntaxTree.ParseText(modelSource)],
            references,
            new(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = new TempFile("dll");
        var emit = modelCompilation.Emit(dllPath);
        Assert.That(emit.Success, Is.True, () => string.Join("\n", emit.Diagnostics));

        var consumer = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText("// consumer")],
            references,
            new(OutputKind.DynamicallyLinkedLibrary));

        var generator = new ScryGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator.AsSourceGenerator()],
            additionalTexts: null,
            parseOptions: null,
            optionsProvider: new TestOptionsProvider(
                new()
                {
                    ["build_property.ScryModelDll"] = dllPath
                }));

        return driver.RunGenerators(consumer);
    }

    static List<MetadataReference> ReferenceAssemblies()
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trusted
            .Split(Path.PathSeparator)
            .Where(_ => _.Length > 0)
            .Select(MetadataReference (_) => MetadataReference.CreateFromFile(_))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(QueryableAttribute).Assembly.Location));
        return references;
    }
}