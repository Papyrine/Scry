using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pneumatic;

namespace Pneumatic.SourceGenerator.Tests;

[TestFixture]
public class GeneratorTests
{
    [Test]
    public Task EntitiesViewPocoAndEnum()
    {
        const string model = """
            using Pneumatic;

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

    static Task VerifyGenerated(string modelSource)
    {
        var references = ReferenceAssemblies();
        var modelCompilation = CSharpCompilation.Create(
            "Sample.Model",
            [CSharpSyntaxTree.ParseText(modelSource)],
            references,
            new(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(Path.GetTempPath(), $"PneumaticModel_{Guid.NewGuid():N}.dll");
        try
        {
            var emit = modelCompilation.Emit(dllPath);
            Assert.That(emit.Success, Is.True, () => string.Join("\n", emit.Diagnostics));

            var consumer = CSharpCompilation.Create(
                "Consumer",
                [CSharpSyntaxTree.ParseText("// consumer")],
                references,
                new(OutputKind.DynamicallyLinkedLibrary));

            var generator = new PneumaticGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators: [generator.AsSourceGenerator()],
                additionalTexts: null,
                parseOptions: null,
                optionsProvider: new TestOptionsProvider(new() { ["build_property.PneumaticModelDll"] = dllPath }));

            driver = driver.RunGenerators(consumer);
            return Verify(driver);
        }
        finally
        {
            if (File.Exists(dllPath))
            {
                File.Delete(dllPath);
            }
        }
    }

    static List<MetadataReference> ReferenceAssemblies()
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trusted
            .Split(Path.PathSeparator)
            .Where(_ => _.Length > 0)
            .Select(_ => (MetadataReference)MetadataReference.CreateFromFile(_))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(QueryableAttribute).Assembly.Location));
        return references;
    }

    sealed class TestOptionsProvider(Dictionary<string, string> values) :
        AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestOptions(values);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    sealed class TestOptions(Dictionary<string, string> values) :
        AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) =>
            values.TryGetValue(key, out value!);
    }
}
