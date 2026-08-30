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
    public Task Hierarchy()
    {
        // Vehicle opts in and so inherits the base model, declaring only its own members. Artwork
        // derives from an opted-in type but never opted in itself, so it is not emitted at all —
        // opting in is a statement about the type it is written on, not about its subclasses.
        const string model = """
            using Scry;

            namespace Sample.Model;

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

            public class Artwork : Asset
            {
                public string Medium { get; set; } = "";
            }
            """;

        return VerifyGenerated(model);
    }

    [Test]
    public Task CollectionNavigation()
    {
        // Lines opts in and is emitted as an aggregable list; Tags is a collection of a type that is
        // not opted in, and Notes is a collection that never asked to be exposed. Both stay invisible.
        // Codes, Scores and Grades are collections of values (EF primitive collections), whose element
        // is spelled exactly as a scalar member of the same type would be — including re-emitting the
        // enum, which is only reachable through the collection.
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
                [QueryableCollection] public List<string> Codes { get; set; } = [];
                [QueryableCollection] public List<int?> Scores { get; set; } = [];
                [QueryableCollection] public List<Grade> Grades { get; set; } = [];
                public List<string> Secrets { get; set; } = [];
            }

            public enum Grade
            {
                Low,
                High
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
            using System.Collections.Generic;
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
                // A collection of the complex type — a JSON array. It opts in like any other
                // collection and is emitted as an aggregable list of the same query model, which is
                // what keeps the emission and the server's introspection (and so the stamp) identical.
                [QueryableCollection] public List<Address> PreviousAddresses { get; set; } = [];
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
    public Task Obsolete()
    {
        // A deprecated source, a deprecated member, and a deprecation with no message. The message on
        // Code carries a quote and a backslash, which the emitted literal has to survive. Department is
        // deprecated as a type, so the navigation to it and its own entry point are uses of an obsolete
        // type inside generated code — what the header's pragma exists for.
        const string model = """
            using System;
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }

                [Obsolete("Use Name instead: \"FullName\" was split in the C:\\Payroll migration.")]
                public string Code { get; set; } = "";

                public string Name { get; set; } = "";

                [Obsolete]
                public bool Active { get; set; }

                public Department? Department { get; set; }
            }

            [Queryable]
            [Obsolete("Flattened into Employee.Name.")]
            public class Department
            {
                public int Id { get; set; }
            }

            // Deprecated as an error on the server. The client is still only warned: the server
            // executes queries against it either way, and a build break would say otherwise.
            [Queryable]
            [Obsolete("Superseded by Employee.Active.", error: true)]
            public class Retired
            {
                public int Id { get; set; }
            }
            """;

        return VerifyGenerated(model);
    }

    // A deprecated model type is used by generated code the consumer cannot edit: every navigation to
    // it, and its own entry point. '<auto-generated/>' does not suppress CS0612/CS0618, so without the
    // header's pragma a consumer building with TreatWarningsAsErrors would fail on those uses. The
    // consumer's own query code is outside these files and still warns.
    [Test]
    public void GeneratedCodeDoesNotWarnOnItsOwnObsoleteTypes()
    {
        const string model = """
            using System;
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                public Department? Department { get; set; }
            }

            [Queryable]
            [Obsolete("Flattened into Employee.Name.")]
            public class Department
            {
                public int Id { get; set; }
            }
            """;

        var compilation = CSharpCompilation.Create(
            "Generated",
            GeneratedSources(model).Select(_ => CSharpSyntaxTree.ParseText(_)),
            ReferenceAssemblies(),
            new(OutputKind.DynamicallyLinkedLibrary));

        // Only the obsolete diagnostics are asserted on: Scry.Client is deliberately not referenced
        // here, so the entry point's own unresolved names are expected and beside the point.
        var obsolete = compilation.GetDiagnostics()
            .Where(_ => _.Id is "CS0612" or "CS0618" or "CS0619")
            .ToList();

        Assert.That(obsolete, Is.Empty, () => string.Join('\n', obsolete));
    }

    // Deprecating something leaves the queryable surface exactly as it was, so the stamp must not move.
    // Were it hashed, marking one member [Obsolete] would report every deployed client as stale — the
    // opposite of what a deprecation window is for.
    [Test]
    public void ObsoleteDoesNotAffectTheSchemaStamp()
    {
        const string bare = """
            using System;
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;

        const string annotated = """
            using System;
            using Scry;

            namespace Sample.Model;

            [Queryable]
            [Obsolete("Going away.")]
            public class Employee
            {
                public int Id { get; set; }
                [Obsolete] public string Name { get; set; } = "";
            }
            """;

        Assert.That(Stamp(annotated), Is.EqualTo(Stamp(bare)));
    }

    // The counterpart to the test above, and the reason the two are worth stating together: [Sensitive]
    // changes what an already-deployed client may do, where [Obsolete] only changes what it is told. A
    // client generated before the marking keeps asking in URLs and starts being refused, so the stamp
    // has to move — that is what turns the refusal into a reported staleness with a fix attached.
    [Test]
    public void SensitiveMovesTheSchemaStamp()
    {
        const string bare = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                public string Ssn { get; set; } = "";
            }
            """;

        const string marked = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                [Sensitive] public string Ssn { get; set; } = "";
            }
            """;

        const string wholeType = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            [Sensitive]
            public class Employee
            {
                public int Id { get; set; }
                public string Ssn { get; set; } = "";
            }
            """;

        Assert.Multiple(() =>
        {
            Assert.That(Stamp(marked), Is.Not.EqualTo(Stamp(bare)));
            Assert.That(Stamp(wholeType), Is.Not.EqualTo(Stamp(bare)));

            // Marking the type is not the same statement as marking its one member, so the two do not
            // collapse onto each other: the type's mark reaches members added later, and any navigation
            // into it.
            Assert.That(Stamp(wholeType), Is.Not.EqualTo(Stamp(marked)));
        });
    }

    [Test]
    public Task Sensitive()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
                [Sensitive] public string Ssn { get; set; } = "";
                public Payroll? Payroll { get; set; }
            }

            [QueryableComplex]
            [Sensitive]
            public class Payroll
            {
                public decimal Salary { get; set; }
            }
            """;

        return Verify(RunGenerator(model));
    }

    static string Stamp(string modelSource) =>
        GeneratedSources(modelSource)
            .SelectMany(_ => _.Split('\n'))
            .Single(_ => _.Contains("public const string SchemaStamp"));

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

    // A source name is also the property the entry point exposes, so one that is not an identifier
    // would emit a ScryQuery that does not parse — in a file the consumer cannot edit. Reported
    // against the model instead, naming the attribute to fix. The server refuses the same name at
    // startup, so which side is built first does not change where the mistake surfaces.
    [Test]
    public Task InvalidSourceNameIsReported()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable(Name = "Sales Region")]
            public class SalesRegion
            {
                public int Id { get; set; }
            }
            """;

        return VerifyGenerated(model);
    }

    // A reserved keyword is a valid wire name but needs an '@' to be a member name, and the wire name
    // carries none — so it is refused rather than silently escaped into something the client would
    // then have to spell differently from the name on the wire.
    [Test]
    public Task KeywordSourceNameIsReported()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable(Name = "class")]
            public class Classroom
            {
                public int Id { get; set; }
            }
            """;

        return VerifyGenerated(model);
    }

    // A source name that is a contextual keyword needs no escaping, so it is emitted as written.
    [Test]
    public void ContextualKeywordSourceNameIsEmitted()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable(Name = "record")]
            public class Recording
            {
                public int Id { get; set; }
            }
            """;

        Assert.That(
            GeneratedSources(model).Any(_ => _.Contains("IQueryable<RecordingQueryModel> record =>")),
            Is.True);
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
        Assert.That(emit.Success, Is.True, () => string.Join('\n', emit.Diagnostics));

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