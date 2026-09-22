/// <summary>
/// The generator reads a model as metadata and the server reads it by reflection, and the two must
/// describe the same surface: a client generated from one is validated by the other, and the stamp
/// each computes is what tells a client it is stale. These run the generator's reader over this
/// project's own model assembly — which carries every shape the two have disagreed on — and compare.
/// </summary>
[TestFixture]
public class LockstepTests
{
    [Test]
    public void GeneratorStampMatchesServerStamp()
    {
        var extract = MetadataModelReader.Read(typeof(TestContext).Assembly.Location);

        Assert.Multiple(() =>
        {
            Assert.That(extract.Error, Is.Null);
            Assert.That(ScryGenerator.ComputeStamp(extract), Is.EqualTo(SharedProcessor.Instance.SchemaStamp));
        });
    }

    [Test]
    public void GeneratorAndServerAgreeOnEveryMember()
    {
        var extract = MetadataModelReader.Read(typeof(TestContext).Assembly.Location);
        var described = SharedProcessor.Instance.Describe();

        // Member by member, not only the stamp: a mismatch then names the member rather than a hash.
        // The generator's spelling is the one it emits (an attachment is a handle, not its bytes),
        // which is what the server's introspection reproduces.
        foreach (var type in described.Types)
        {
            var generated = extract.Sources.Single(_ => _.ModelName == type.Model);
            var serverMembers = type.Members.Select(_ => $"{_.Name} {_.TypeDisplay}").Order(StringComparer.Ordinal);
            var generatorMembers = generated.Properties.Select(_ => $"{_.Name} {ScryGenerator.Display(_)}").Order(StringComparer.Ordinal);
            Assert.That(generatorMembers, Is.EqualTo(serverMembers), type.Model);
        }
    }

    // Command by command, for the same reason: the generator's payload is what a client sends and the
    // server's is what it binds, and the key and result they name have to be the same ones.
    [Test]
    public void GeneratorAndServerAgreeOnEveryCommand()
    {
        var extract = MetadataModelReader.Read(typeof(TestContext).Assembly.Location);
        var described = SharedProcessor.Instance.Describe();

        Assert.That(extract.Problems, Is.Empty);
        Assert.That(extract.Commands.Select(_ => _.Name), Is.EqualTo(described.Commands.Select(_ => _.Name)));
        foreach (var command in described.Commands)
        {
            var generated = extract.Commands.Single(_ => _.Name == command.Name);
            Assert.Multiple(() =>
            {
                Assert.That(generated.Target, Is.EqualTo(command.Target), command.Name);
                Assert.That(generated.Keys, Is.EqualTo(command.Keys ?? []), command.Name);
                Assert.That(
                    generated.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}").Order(StringComparer.Ordinal),
                    Is.EqualTo(command.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}").Order(StringComparer.Ordinal)),
                    command.Name);
                Assert.That(generated.ResultName, Is.EqualTo(command.Result?.Name), command.Name);
                Assert.That(generated.Obsolete, Is.EqualTo(command.Obsolete), command.Name);
            });
        }

        foreach (var result in described.Commands.Select(_ => _.Result).OfType<ScryResultInfo>())
        {
            var generated = extract.Results.Single(_ => _.Name == result.Name);
            Assert.That(
                generated.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}").Order(StringComparer.Ordinal),
                Is.EqualTo(result.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}").Order(StringComparer.Ordinal)),
                result.Name);
        }
    }

    // What the server refuses of a command, each of which the generator reports as a diagnostic. Plain
    // fixture types, opted into nothing, so they poison no schema built over this assembly.
    [Test]
    public void RefusesACommandThatCannotBeCreated()
    {
        var exception = Assert.Throws<Exception>(() => Schema.EnsureConcreteCommand(typeof(AbstractCommand)));

        Assert.That(exception!.Message, Does.Contain("is not a concrete class with a public parameterless constructor"));
    }

    [Test]
    public void RefusesQueryIgnoreOnACommandProperty()
    {
        var exception = Assert.Throws<Exception>(() => Schema.CommandPayload(typeof(QueryIgnoredCommand), typeof(LockstepTests).Assembly));

        Assert.That(exception!.Message, Does.Contain("carries [QueryIgnore], which hides a member from queries and means nothing on a command"));
    }

    [Test]
    public void RefusesAPayloadPropertyNoCommandCanCarry()
    {
        var exception = Assert.Throws<Exception>(() => Schema.CommandPayload(typeof(ObjectCommand), typeof(LockstepTests).Assembly));

        Assert.That(exception!.Message, Does.Contain("'ObjectCommand.Anything' is a 'Object', which a command cannot carry"));
    }

    [Test]
    public void RefusesAPayloadEnumFromAnotherAssembly()
    {
        var exception = Assert.Throws<Exception>(() => Schema.CommandPayload(typeof(ForeignEnumCommand), typeof(LockstepTests).Assembly));

        Assert.That(exception!.Message, Does.Contain("'ForeignEnumCommand.Day' is a 'DayOfWeek', which a command cannot carry"));
    }

    [Test]
    public void KeepsIgnoredAndReadOnlyPropertiesOutOfThePayload()
    {
        var payload = Schema.CommandPayload(typeof(MixedCommand), typeof(LockstepTests).Assembly);

        Assert.That(payload.Select(_ => _.Name), Is.EqualTo(["Id", "Name"]));
    }

    [Test]
    public void BindsTheTypeNameKeyConvention()
    {
        var target = Schema.BuildTypeMeta(typeof(Badge), []);
        var payload = Schema.CommandPayload(typeof(PrefixedKeyCommand), typeof(LockstepTests).Assembly);

        var keys = Schema.BindCommandKeys(typeof(PrefixedKeyCommand), target, payload);

        Assert.That(keys.Select(_ => (_.Key.Name, _.Payload.Name)), Is.EqualTo(new[] {("Id", "BadgeId")}));
    }

    [Test]
    public void RefusesAnAmbiguousKey()
    {
        var target = Schema.BuildTypeMeta(typeof(Badge), []);
        var payload = Schema.CommandPayload(typeof(AmbiguousKeyCommand), typeof(LockstepTests).Assembly);

        var exception = Assert.Throws<Exception>(() => Schema.BindCommandKeys(typeof(AmbiguousKeyCommand), target, payload));

        Assert.That(exception!.Message, Does.Contain("carries both 'Id' and 'BadgeId', so which one is the key of 'Badge' is ambiguous"));
    }

    [Test]
    public void RefusesAKeyOfTheWrongType()
    {
        var target = Schema.BuildTypeMeta(typeof(Badge), []);
        var payload = Schema.CommandPayload(typeof(WrongKeyCommand), typeof(LockstepTests).Assembly);

        var exception = Assert.Throws<Exception>(() => Schema.BindCommandKeys(typeof(WrongKeyCommand), target, payload));

        Assert.That(exception!.Message, Does.Contain("keyed by 'Id', but carries no 'int' property named 'Id' or 'BadgeId'"));
    }

    [Test]
    public void RefusesAResultFromAnotherAssembly()
    {
        var exception = Assert.Throws<Exception>(() => Schema.ResultProperties(typeof(MixedCommand), typeof(Uri), typeof(LockstepTests).Assembly));

        Assert.That(exception!.Message, Does.Contain("answers with 'Uri', which is declared in assembly"));
    }

    // The base's members are the derived type's own on both sides; the override is described once,
    // the indexer never, and an array is a collection of its element.
    [Test]
    public void AnUnannotatedBaseContributesItsMembersOnce()
    {
        var invoice = SharedProcessor.Instance.Describe().Types.Single(_ => _.Model == "InvoiceQueryModel");

        // AuditTrail is hidden on the base and stays hidden through the override; Reviewer is marked
        // on the base and stays marked through it. Both sides describe both that way.
        string[] expected = ["CreatedBy", "Id", "Notes", "Number", "Reviewer", "Tags", "Weights"];
        Assert.Multiple(() =>
        {
            Assert.That(invoice.Base, Is.Null);
            Assert.That(invoice.Members.Select(_ => _.Name), Is.EqualTo(expected));
            Assert.That(invoice.Members.Single(_ => _.Name == "Reviewer").IsSensitive, Is.True);
            Assert.That(invoice.Members.Single(_ => _.Name == "Tags").TypeDisplay, Is.EqualTo("global::System.Collections.Generic.IReadOnlyList<string>"));
        });
    }

    // What the server refuses because the generator could not read it. Plain fixture types, opted
    // into nothing, so they poison no schema built over this assembly.
    // Built at runtime rather than declared here: a type carrying an opt-in attribute in this assembly
    // would be swept into every schema built over it, and this one is refused on sight.
    [Test]
    public void RefusesATypeOptedInTwice()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new("OptedInTwice"), AssemblyBuilderAccess.Run);
        var builder = assembly.DefineDynamicModule("OptedInTwice").DefineType("Both", TypeAttributes.Public);
        builder.SetCustomAttribute(new(typeof(QueryableAttribute).GetConstructor([])!, []));
        builder.SetCustomAttribute(new(typeof(QueryableComplexAttribute).GetConstructor([])!, []));
        var type = builder.CreateType();

        var exception = Assert.Throws<Exception>(() => Schema.EnsureOneOptIn(type));

        Assert.That(exception!.Message, Does.Contain("'Both' carries [Queryable] and [QueryableComplex]"));
    }

    [Test]
    public void RefusesAMemberInheritedFromAnotherAssembly()
    {
        var exception = Assert.Throws<Exception>(() => Schema.BuildTypeMeta(typeof(ForeignBaseRow), []));

        Assert.That(exception!.Message, Does.Contain("inherited from 'List`1'"));
    }

    [Test]
    public void RefusesAnEnumFromAnotherAssembly()
    {
        var exception = Assert.Throws<Exception>(() => Schema.BuildTypeMeta(typeof(ForeignEnumRow), []));

        Assert.That(exception!.Message, Does.Contain("'DayOfWeek', an enum declared in assembly"));
    }

    [Test]
    public void RefusesACollectionShapeTheGeneratorDoesNotRead()
    {
        var exception = Assert.Throws<Exception>(() => Schema.BuildTypeMeta(typeof(OddCollectionRow), []));

        Assert.That(exception!.Message, Does.Contain("a collection shape the generator does not read"));
    }

    [Test]
    public void ExposableCollectionShapesAreTheGeneratorsList() =>
        Assert.Multiple(() =>
        {
            Assert.That(Schema.ExposableCollectionElement(typeof(string[])), Is.EqualTo(typeof(string)));
            Assert.That(Schema.ExposableCollectionElement(typeof(List<int>)), Is.EqualTo(typeof(int)));
            Assert.That(Schema.ExposableCollectionElement(typeof(IReadOnlyList<Guid>)), Is.EqualTo(typeof(Guid)));
            Assert.That(Schema.ExposableCollectionElement(typeof(int[,])), Is.Null);
            Assert.That(Schema.ExposableCollectionElement(typeof(SortedSet<int>)), Is.Null);
            Assert.That(Schema.ExposableCollectionElement(typeof(string)), Is.Null);
        });

    // ReSharper disable UnusedMember.Local
    class ForeignBaseRow :
        List<int>;

    class ForeignEnumRow
    {
        public DayOfWeek Day { get; set; }
    }

    class OddCollectionRow
    {
        [QueryableCollection]
        public SortedSet<int> Values { get; set; } = [];
    }

    abstract class AbstractCommand
    {
        public int Id { get; set; }
    }

    class QueryIgnoredCommand
    {
        [QueryIgnore]
        public int Id { get; set; }
    }

    class ObjectCommand
    {
        public object? Anything { get; set; }
    }

    class ForeignEnumCommand
    {
        public DayOfWeek Day { get; set; }
    }

    class MixedCommand
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        [CommandIgnore]
        public string By { get; set; } = "";

        public int ReadOnly => Id;
    }

    class Badge
    {
        public int Id { get; set; }
    }

    class PrefixedKeyCommand
    {
        public int BadgeId { get; set; }
    }

    class AmbiguousKeyCommand
    {
        public int Id { get; set; }
        public int BadgeId { get; set; }
    }

    class WrongKeyCommand
    {
        public long Id { get; set; }
    }
    // ReSharper restore UnusedMember.Local
}
