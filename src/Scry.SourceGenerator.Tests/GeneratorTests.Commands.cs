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

            // Optional members, carried as the model declares them: a nullable reference is one on
            // the client too, so a caller leaves it unset rather than writing `value!`.
            public string? Nickname { get; set; }
            public Guid? Team { get; set; }
            public byte[]? Photo { get; set; }
            public List<string>? Aliases { get; set; }
        }

        public class EmployeeCreated
        {
            public int Id { get; set; }
            public string? Badge { get; set; }
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

    // A command's target is read before inherited members are folded into the base model, so an override
    // of a base's [Key] is met twice along the chain. It is one key, bound once: bound twice, the stamp
    // named it twice where the server names it once, and every client reported itself stale.
    [Test]
    public async Task AnOverriddenKeyBindsOnce()
    {
        const string model = """
            using System.ComponentModel.DataAnnotations;
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Asset
            {
                [Key] public virtual int Id { get; set; }
            }

            [Queryable]
            public class Vehicle : Asset
            {
                public override int Id { get; set; }
            }

            [Command(typeof(Vehicle))]
            public class Touch
            {
                public int Id { get; set; }
            }
            """;

        var commands = (await GeneratedSources(model)).Single(_ => _.Contains("public sealed class ScryCommands"));

        await Assert.That(commands).Contains("""[global::Scry.ScryCommand("Touch", Target = "Vehicle", Keys = new[] {"Id"})]""");
    }

    // The server binds a payload into the public properties it can both read and set, and describes a
    // result by the public ones it can read; a declaration outside that set leaves its name to a base's,
    // as reflection does. The generator once let it claim the name, dropped it, and lost the base's.
    [Test]
    public async Task ANewPropertyThePayloadCannotUseLeavesTheBasesToTheCommand()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

            public class Payload
            {
                public int Id { get; set; }
                public string Text { get; set; } = "";
            }

            [Command]
            public class Touch : Payload
            {
                public new string Text => "";
            }

            public class Answer
            {
                public int Count { get; set; }
            }

            public class Detailed : Answer
            {
                protected new int Count { get; set; }
            }

            [Command(Result = typeof(Detailed))]
            public class Poke
            {
                public int Id { get; set; }
            }
            """;

        var commands = (await GeneratedSources(model)).Single(_ => _.Contains("public sealed class ScryCommands"));

        using (Assert.Multiple())
        {
            await Assert.That(commands).Contains("public string Text { get; init; } = null!;");
            await Assert.That(commands).Contains("public int Count { get; init; }");
        }
    }

    // A nullable annotation is read from the property's own [Nullable] flag, or from the
    // [NullableContext] the compiler puts on the class when every member shares one — which is what a
    // class of nothing but `string?` members carries. Whichever, it is a note to the caller about what
    // may be left unset, and moves nothing the server reads: the stamp is the same either way.
    [Test]
    public async Task ANullableReferenceIsEmittedNullableAndDoesNotAffectTheSchemaStamp()
    {
        const string required = """
            using System.Collections.Generic;
            using Scry;

            namespace Sample.Model;

            [Command]
            public class Touch
            {
                public string Note { get; set; } = "";
                public List<string> Tags { get; set; } = [];
            }
            """;

        const string optional = """
            using System.Collections.Generic;
            using Scry;

            namespace Sample.Model;

            [Command]
            public class Touch
            {
                public string? Note { get; set; }
                public List<string>? Tags { get; set; }
            }
            """;

        var commands = (await GeneratedSources(optional)).Single(_ => _.Contains("public sealed class ScryCommands"));

        using (Assert.Multiple())
        {
            await Assert.That(commands).Contains("public string? Note { get; init; }");
            await Assert.That(commands).Contains("public global::System.Collections.Generic.IReadOnlyList<string>? Tags { get; init; }");
            await Assert.That(commands).DoesNotContain("null!");
            await Assert.That(await Stamp(optional)).IsEqualTo(await Stamp(required));
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
        "SCRY011",
        """
        [Command(Name = "client")] public class Touch { public int Id { get; set; } }
        """,
        "The name 'client' is generated twice: as the facade's client field and as the facade method for 'Touch'.")]
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
        "SCRY019",
        """
        public class @lock { public int Id { get; set; } }
        [Command(Result = typeof(@lock))] public class Touch { public int Id { get; set; } }
        """,
        "The result class 'lock' is emitted as a type of its own name, which cannot be written as a C# type name, so nothing can be generated for it.")]
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
