public partial class GeneratorTests
{
    const string bclEnumModel = """
        using System;
        using System.Collections.Generic;
        using Scry;

        namespace Sample.Model;

        public enum Stage { Draft, Sitting }

        [Queryable]
        public class Sitting
        {
            public int Id { get; set; }
            public Stage Stage { get; set; }
            public DayOfWeek DayOfWeek { get; set; }
            public DayOfWeek? Recess { get; set; }
            public DateTimeKind Clock { get; set; }
            [QueryableCollection] public List<DayOfWeek> Alternates { get; set; } = [];
        }

        [Command(typeof(Sitting))]
        public class Reschedule
        {
            public int Id { get; set; }
            public DayOfWeek Day { get; set; }
            public List<DateTimeKind?> Clocks { get; set; } = [];
        }
        """;

    // An enum of the base class library is on every client already: a member typed as one is spelled
    // as the BCL type itself, on a query model and on a command alike, and ScryEnums.g.cs declares only
    // the model's own enum.
    [Test]
    public Task BclEnums() =>
        VerifyGenerated(bclEnumModel);

    [Test]
    public async Task BclEnumsAreNeitherReEmittedNorReported()
    {
        var driver = await RunGenerator(bclEnumModel);
        var result = driver.GetRunResult();
        var sources = result.Results
            .SelectMany(_ => _.GeneratedSources)
            .ToList();
        var enums = sources.Single(_ => _.HintName == "ScryEnums.g.cs").SourceText.ToString();

        using (Assert.Multiple())
        {
            await Assert.That(enums).Contains("public enum Stage");
            await Assert.That(enums).DoesNotContain("DayOfWeek");
            await Assert.That(enums).DoesNotContain("DateTimeKind");
            await Assert.That(result.Diagnostics.Where(_ => _.GetMessage().Contains("DayOfWeek") || _.GetMessage().Contains("DateTimeKind"))).IsEmpty();
        }

        // The generated code names the BCL types, which resolve without anything re-emitted for them.
        // Scry.Client is deliberately not referenced, so only diagnostics naming the two are asserted on.
        var compilation = CSharpCompilation.Create(
            "Generated",
            sources.Select(_ => CSharpSyntaxTree.ParseText(_.SourceText.ToString())),
            ReferenceAssemblies(),
            new(OutputKind.DynamicallyLinkedLibrary));
        var unresolved = compilation.GetDiagnostics()
            .Where(_ => _.GetMessage().Contains("DayOfWeek") || _.GetMessage().Contains("DateTimeKind"))
            .ToList();

        await Assert.That(unresolved).IsEmpty().Because(string.Join('\n', unresolved));
    }

    // A model declaring its own System.DayOfWeek holds a definition, not a reference: the enum is the
    // model's, re-emitted with the values the model gives it, exactly as under any other name.
    [Test]
    public async Task AModelDeclaredEnumNamedLikeABclOneIsTheModels()
    {
        const string model = """
            using Scry;

            namespace System
            {
                public enum DayOfWeek { Moonday, Tuesday }
            }

            namespace Sample.Model
            {
                [Queryable]
                public class Sitting
                {
                    public int Id { get; set; }
                    public System.DayOfWeek Day { get; set; }
                }
            }
            """;

        var sources = await GeneratedSources(model);

        using (Assert.Multiple())
        {
            await Assert.That(sources.Single(_ => _.Contains("public enum DayOfWeek"))).Contains("Moonday");
            await Assert.That(sources.Single(_ => _.Contains("class SittingQueryModel"))).Contains("public DayOfWeek Day { get; init; }");
        }
    }
}
