public partial class GeneratorTests
{
    const string commandModel = """
        using System;
        using System.Collections.Generic;
        using Scry;

        namespace Sample.Model;

        public enum Status { FullTime, PartTime, Contractor }

        [Queryable]
        public class Department
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        [Queryable]
        public class Employee
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public Status Status { get; set; }
            public bool Active { get; set; }
            public Department? Department { get; set; }
        }

        [Command(typeof(Employee), Policy = typeof(DeleteEmployeePolicy))]
        public class DeleteEmployee
        {
            public int Id { get; set; }
        }

        [Command(typeof(Employee))]
        public class RenameEmployee
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";

            [CommandIgnore]
            public string RenamedBy { get; set; } = "";
        }

        [Command(Result = typeof(EmployeeCreated))]
        [Obsolete("Hire through the portal.")]
        public class CreateEmployee
        {
            public string Name { get; set; } = "";
            public int DepartmentId { get; set; }
            public Status Status { get; set; }
            public List<string> Tags { get; set; } = [];
            public DateOnly? Starts { get; set; }
        }

        public class EmployeeCreated
        {
            public int Id { get; set; }
        }

        // Server-only: the generator never reads a command's policy.
        public class DeleteEmployeePolicy;
        """;

    [Test]
    public Task Commands() =>
        VerifyGenerated(commandModel);

    // The key may be named after the key member or prefixed with the entity's name, as EF's own
    // convention for a foreign key would spell it.
    [Test]
    public async Task ACommandKeyMayCarryTheEntityName()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
            }

            [Command(typeof(Employee))]
            public class Promote
            {
                public int EmployeeId { get; set; }
            }
            """;

        var commands = (await GeneratedSources(model)).Single(_ => _.Contains("public sealed class ScryCommands"));

        await Assert.That(commands).Contains("""[global::Scry.ScryCommand("Promote", Target = "Employee", Keys = new[] {"EmployeeId"})]""");
    }

    [Test]
    public async Task ACommandNameOverrideNamesTheClassTheMethodAndTheCapability()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
            }

            [Command(typeof(Employee), Name = "Fire")]
            public class TerminateEmployee
            {
                public int Id { get; set; }
            }
            """;

        var sources = await GeneratedSources(model);
        var commands = sources.Single(_ => _.Contains("public sealed class ScryCommands"));
        var employee = sources.Single(_ => _.Contains("public class EmployeeQueryModel"));

        using (Assert.Multiple())
        {
            await Assert.That(commands).Contains("public sealed class Fire");
            await Assert.That(commands).Contains("Fire(\n        global::Scry.Generated.Fire command,").Or.Contains("Fire(\r\n        global::Scry.Generated.Fire command,");
            await Assert.That(commands).Contains("public bool CanFire => client.Can(\"Fire\");");
            await Assert.That(employee).Contains("public bool CanFire { get; init; }");
            await Assert.That(commands).DoesNotContain("TerminateEmployee");
        }
    }

    // A model declaring no command emits exactly what it did before commands existed — no facade, no
    // capability — and its stamp is unchanged, which the untouched snapshots above pin. Declaring one
    // moves the stamp: it changes what a deployed client can send.
    [Test]
    public async Task ACommandMovesTheSchemaStamp()
    {
        const string bare = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
            }
            """;

        const string withCommand = """
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
            }

            [Command(typeof(Employee))]
            public class Touch
            {
                public int Id { get; set; }
            }
            """;

        using (Assert.Multiple())
        {
            await Assert.That(await Stamp(withCommand)).IsNotEqualTo(await Stamp(bare));
            await Assert.That((await GeneratedSources(bare)).Any(_ => _.Contains("ScryCommands"))).IsFalse();
        }
    }

    // Deprecating a command, like deprecating a member, is a note to whoever rebuilds a client and
    // leaves what a deployed one may send exactly as it was.
    [Test]
    public async Task ObsoleteOnACommandDoesNotAffectTheSchemaStamp()
    {
        const string bare = """
            using Scry;

            namespace Sample.Model;

            [Command]
            public class Touch
            {
                public int Id { get; set; }
            }
            """;

        const string annotated = """
            using System;
            using Scry;

            namespace Sample.Model;

            [Command]
            [Obsolete]
            public class Touch
            {
                [Obsolete] public int Id { get; set; }
            }
            """;

        await Assert.That(await Stamp(annotated)).IsEqualTo(await Stamp(bare));
    }

    // Every way a command can be misdeclared is reported, and — like a conflicting opt-in — nothing is
    // emitted: the server refuses the same model at startup, so any code generated from it would be
    // a client for a surface no server serves.
    [Test]
    [Arguments(
        "SCRY009",
        """
        [QueryableView] public class Summary { public int Id { get; set; } }
        [Command(typeof(Summary))] public class Touch { public int Id { get; set; } }
        """,
        "'Touch' targets 'Summary', which is not a [Queryable] entity.")]
    [Arguments(
        "SCRY009",
        """
        public class Plain { public int Id { get; set; } }
        [Command(typeof(Plain))] public class Touch { public int Id { get; set; } }
        """,
        "'Touch' targets 'Plain', which is not a [Queryable] entity.")]
    [Arguments(
        "SCRY010",
        """
        [Command(typeof(Employee))] public class Touch { public string Note { get; set; } = ""; }
        """,
        "'Touch' targets 'Employee', keyed by 'Id', but carries no 'int' property named 'Id' or 'EmployeeId'.")]
    [Arguments(
        "SCRY010",
        "[Command(typeof(Employee))] public class Touch { public long Id { get; set; } }",
        "'Touch' targets 'Employee', keyed by 'Id', but carries no 'int' property named 'Id' or 'EmployeeId'.")]
    [Arguments(
        "SCRY010",
        "[Command(typeof(Employee))] public class Touch { public int Id { get; set; } public int EmployeeId { get; set; } }",
        "'Touch' carries both 'Id' and 'EmployeeId', so which one is the key of 'Employee' is ambiguous.")]
    [Arguments(
        "SCRY011",
        """
        [Command(Name = "Touch")] public class First { public int Id { get; set; } }
        [Command(Name = "Touch")] public class Second { public int Id { get; set; } }
        """,
        "The name 'Touch' is generated twice")]
    [Arguments(
        "SCRY011",
        """
        public enum Touch { One }
        [Command] public class Nudge { public Touch Value { get; set; } }
        [Command(Name = "Touch")] public class Poke { public int Id { get; set; } }
        """,
        "The name 'Touch' is generated twice: as an enum and as the command 'Poke'.")]
    [Arguments(
        "SCRY012",
        """
        [Command(Name = "class")] public class Touch { public int Id { get; set; } }
        """,
        "The command name 'class' on 'Touch' cannot be written as a C# member name")]
    [Arguments(
        "SCRY013",
        "[Command] public class Touch { [QueryIgnore] public int Id { get; set; } }",
        "'Touch.Id' carries [QueryIgnore], which hides a member from queries and means nothing on a command.")]
    [Arguments(
        "SCRY014",
        "[Command] public class Touch { public Employee? Employee { get; set; } }",
        "'Touch.Employee' is not a type a command can carry.")]
    [Arguments(
        "SCRY014",
        "[Command] public class Touch { public object? Anything { get; set; } }",
        "'Touch.Anything' is not a type a command can carry.")]
    [Arguments(
        "SCRY015",
        """
        public class Touched { public object? Anything { get; set; } }
        [Command(Result = typeof(Touched))] public class Touch { public int Id { get; set; } }
        """,
        "'Touch' answers with 'Touched', whose property 'Anything' is not a type a result can carry.")]
    [Arguments(
        "SCRY015",
        "[Command(Result = typeof(System.Uri))] public class Touch { public int Id { get; set; } }",
        "'Touch' answers with 'Uri', which is not declared in the model assembly.")]
    [Arguments(
        "SCRY015",
        """
        public class Envelope<T> { public T Value { get; set; } = default!; }
        [Command(Result = typeof(Envelope<int>))] public class Touch { public int Id { get; set; } }
        """,
        "'Touch' answers with 'Envelope', which is generic.")]
    [Arguments(
        "SCRY015",
        """
        public class Envelope<T> { public int Count { get; set; } }
        [Command(Result = typeof(Envelope<>))] public class Touch { public int Id { get; set; } }
        """,
        "'Touch' answers with 'Envelope', which is generic.")]
    [Arguments(
        "SCRY016",
        """
        [Queryable] public class Badge { public int Id { get; set; } public bool CanTouch { get; set; } }
        [Command(typeof(Badge))] public class Touch { public int Id { get; set; } }
        """,
        "'Touch' would add 'CanTouch' to 'Badge', which already has a member of that name.")]
    [Arguments(
        "SCRY017",
        "[Command] public abstract class Touch { public int Id { get; set; } }",
        "'Touch' carries [Command] but is not a concrete class with a public parameterless constructor.")]
    [Arguments(
        "SCRY017",
        "[Command] public class Touch { public Touch(int id) => Id = id; public int Id { get; set; } }",
        "'Touch' carries [Command] but is not a concrete class with a public parameterless constructor.")]
    [Arguments(
        "SCRY008",
        "[Queryable] [Command] public class Both { public int Id { get; set; } }",
        "'Both' carries [Queryable] and [Command].")]
    public async Task AMisdeclaredCommandIsReported(string id, string declarations, string message)
    {
        var model = $$"""
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Employee
            {
                public int Id { get; set; }
            }

            {{declarations}}
            """;

        var result = (await RunGenerator(model)).GetRunResult();

        var diagnostic = result.Diagnostics.FirstOrDefault(_ => _.Id == id);
        await Assert.That(diagnostic).IsNotNull().Because(string.Join('\n', result.Diagnostics));
        using (Assert.Multiple())
        {
            await Assert.That(diagnostic!.GetMessage()).StartsWith(message);
            await Assert.That(result.Results.SelectMany(_ => _.GeneratedSources)).IsEmpty();
        }
    }
}
