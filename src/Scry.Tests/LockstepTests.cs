/// <summary>
/// The generator reads a model as metadata and the server reads it by reflection, and the two must
/// describe the same surface: a client generated from one is validated by the other, and the stamp
/// each computes is what tells a client it is stale. These run the generator's reader over this
/// project's own model assembly — which carries every shape the two have disagreed on — and compare.
/// </summary>
public class LockstepTests
{
    [Test]
    public async Task GeneratorStampMatchesServerStamp()
    {
        var extract = MetadataModelReader.Read(typeof(TestContext).Assembly.Location);

        using (Assert.Multiple())
        {
            await Assert.That(extract.Error).IsNull();
            await Assert.That(ScryGenerator.ComputeStamp(extract)).IsEqualTo(SharedProcessor.Instance.SchemaStamp);
        }
    }

    [Test]
    public Task GeneratorAndServerAgreeOnEveryMember() =>
        AgreeOnEveryMember(
            MetadataModelReader.Read(typeof(TestContext).Assembly.Location),
            SharedProcessor.Instance.Describe());

    [Test]
    public Task GeneratorAndServerAgreeOnEveryCommand() =>
        AgreeOnEveryCommand(
            MetadataModelReader.Read(typeof(TestContext).Assembly.Location),
            SharedProcessor.Instance.Describe());

    // Member by member, not only the stamp: a mismatch then names the member rather than a hash.
    // The generator's spelling is the one it emits (an attachment is a handle, not its bytes),
    // which is what the server's introspection reproduces. The deprecation is compared too: it is out
    // of the stamp, but the generated member and the explorer's are meant to carry the same one.
    internal static async Task AgreeOnEveryMember(ModelExtract extract, ScryIntrospection described)
    {
        foreach (var type in described.Types)
        {
            var generated = extract.Sources.Single(_ => _.ModelName == type.Model);
            var serverMembers = type.Members.Select(_ => $"{_.Name} {_.TypeDisplay}{Deprecation(_.Obsolete)}").Order(StringComparer.Ordinal);
            var generatorMembers = generated.Properties.Select(_ => $"{_.Name} {ScryGenerator.Display(_)}{Deprecation(_.Obsolete)}").Order(StringComparer.Ordinal);
            await Assert.That(generatorMembers).IsEquivalentTo(serverMembers, CollectionOrdering.Matching).Because(type.Model);
        }
    }

    static string Deprecation(string? obsolete) =>
        obsolete is null ? "" : $" [Obsolete(\"{obsolete}\")]";

    // Command by command, for the same reason: the generator's payload is what a client sends and the
    // server's is what it binds, and the key and result they name have to be the same ones.
    internal static async Task AgreeOnEveryCommand(ModelExtract extract, ScryIntrospection described)
    {
        await Assert.That(extract.Problems).IsEmpty();
        await Assert.That(extract.Commands.Select(_ => _.Name)).IsEquivalentTo(described.Commands.Select(_ => _.Name), CollectionOrdering.Matching);
        foreach (var command in described.Commands)
        {
            var generated = extract.Commands.Single(_ => _.Name == command.Name);
            using (Assert.Multiple())
            {
                await Assert.That(generated.Target).IsEqualTo(command.Target).Because(command.Name);
                await Assert.That(generated.Keys).IsEquivalentTo(command.Keys ?? [], CollectionOrdering.Matching).Because(command.Name);
                await Assert.That(generated.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}{Deprecation(_.Obsolete)}").Order(StringComparer.Ordinal)).IsEquivalentTo(command.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}{Deprecation(_.Obsolete)}").Order(StringComparer.Ordinal), CollectionOrdering.Matching).Because(command.Name);
                await Assert.That(generated.ResultName).IsEqualTo(command.Result?.Name).Because(command.Name);
                await Assert.That(generated.Obsolete).IsEqualTo(command.Obsolete).Because(command.Name);
            }
        }

        foreach (var result in described.Commands.Select(_ => _.Result).OfType<ScryResultInfo>())
        {
            var generated = extract.Results.Single(_ => _.Name == result.Name);
            await Assert.That(generated.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}{Deprecation(_.Obsolete)}").Order(StringComparer.Ordinal)).IsEquivalentTo(result.Properties.Select(_ => $"{_.Name} {_.TypeDisplay}{Deprecation(_.Obsolete)}").Order(StringComparer.Ordinal), CollectionOrdering.Matching).Because(result.Name);
        }
    }

    // What the server refuses of a command, each of which the generator reports as a diagnostic. Plain
    // fixture types, opted into nothing, so they poison no schema built over this assembly.
    [Test]
    public async Task RefusesACommandThatCannotBeCreated()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.EnsureConcreteCommand(typeof(AbstractCommand)));

        await Assert.That(exception.Message).Contains("is not a concrete class with a public parameterless constructor");
    }

    [Test]
    public async Task RefusesQueryIgnoreOnACommandProperty()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.CommandPayload(typeof(QueryIgnoredCommand), typeof(LockstepTests).Assembly));

        await Assert.That(exception.Message).Contains("carries [QueryIgnore], which hides a member from queries and means nothing on a command");
    }

    [Test]
    public async Task RefusesAPayloadPropertyNoCommandCanCarry()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.CommandPayload(typeof(ObjectCommand), typeof(LockstepTests).Assembly));

        await Assert.That(exception.Message).Contains("'ObjectCommand.Anything' is a 'Object', which a command cannot carry");
    }

    [Test]
    public async Task RefusesAPayloadEnumFromAnotherAssembly()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.CommandPayload(typeof(ForeignEnumCommand), typeof(LockstepTests).Assembly));

        await Assert.That(exception.Message).IsEqualTo("'ForeignEnumCommand.Comparison' is a 'StringComparison', which a command cannot carry. A payload property is a scalar, an enum declared in the model, a byte[], a nullable of those, or a list of them; anything the server fills itself belongs behind [CommandIgnore].");
    }

    // A BCL enum is on every client already, so a payload may carry one — as a value, a nullable, or a
    // list — and the generator classifies it the same way (CompiledLockstepTests.BclEnums).
    [Test]
    public async Task AcceptsPayloadBclEnums()
    {
        var payload = Schema.CommandPayload(typeof(BclEnumCommand), typeof(LockstepTests).Assembly);

        await Assert.That(payload.Select(_ => _.Name)).IsEquivalentTo(["Alternates", "Clock", "Day", "Recess"], CollectionOrdering.Matching);
    }

    // The four BCL-enum members of Sitting, spelled identically on both sides as the BCL type, and
    // described as an enum by neither: no client re-emits a type it already has.
    [Test]
    public async Task BclEnumsAreSpelledAsThemselvesAndNeverDescribed()
    {
        var extract = MetadataModelReader.Read(typeof(TestContext).Assembly.Location);
        var described = SharedProcessor.Instance.Describe();

        var generated = extract.Sources
            .Single(_ => _.ModelName == "SittingQueryModel")
            .Properties
            .Where(_ => _.Name != "Id" && _.Name != "Name")
            .Select(_ => $"{_.Name} {ScryGenerator.Display(_)}")
            .Order(StringComparer.Ordinal);
        var served = described.Types
            .Single(_ => _.Model == "SittingQueryModel")
            .Members
            .Where(_ => _.Name != "Id" && _.Name != "Name")
            .Select(_ => $"{_.Name} {_.TypeDisplay}")
            .Order(StringComparer.Ordinal);

        string[] expected =
        [
            "Alternates global::System.Collections.Generic.IReadOnlyList<global::System.DayOfWeek>",
            "Clock global::System.DateTimeKind",
            "DayOfWeek global::System.DayOfWeek",
            "Recess global::System.DayOfWeek?"
        ];
        using (Assert.Multiple())
        {
            await Assert.That(generated).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(served).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(extract.Enums.Select(_ => _.Name)).DoesNotContain("DayOfWeek").And.DoesNotContain("DateTimeKind");
            await Assert.That(described.Enums.Select(_ => _.Name)).DoesNotContain("DayOfWeek").And.DoesNotContain("DateTimeKind");
        }
    }

    [Test]
    public async Task KeepsIgnoredAndReadOnlyPropertiesOutOfThePayload()
    {
        var payload = Schema.CommandPayload(typeof(MixedCommand), typeof(LockstepTests).Assembly);

        await Assert.That(payload.Select(_ => _.Name)).IsEquivalentTo(["Id", "Name"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task BindsTheTypeNameKeyConvention()
    {
        var target = Schema.BuildTypeMeta(typeof(Badge), []);
        var payload = Schema.CommandPayload(typeof(PrefixedKeyCommand), typeof(LockstepTests).Assembly);

        var keys = Schema.BindCommandKeys(typeof(PrefixedKeyCommand), target, payload);

        await Assert.That(keys.Select(_ => (_.Key.Name, _.Payload.Name))).IsEquivalentTo([("Id", "BadgeId")], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RefusesAnAmbiguousKey()
    {
        var target = Schema.BuildTypeMeta(typeof(Badge), []);
        var payload = Schema.CommandPayload(typeof(AmbiguousKeyCommand), typeof(LockstepTests).Assembly);

        var exception = Assert.ThrowsExactly<Exception>(() => Schema.BindCommandKeys(typeof(AmbiguousKeyCommand), target, payload));

        await Assert.That(exception.Message).Contains("carries both 'Id' and 'BadgeId', so which one is the key of 'Badge' is ambiguous");
    }

    [Test]
    public async Task RefusesAKeyOfTheWrongType()
    {
        var target = Schema.BuildTypeMeta(typeof(Badge), []);
        var payload = Schema.CommandPayload(typeof(WrongKeyCommand), typeof(LockstepTests).Assembly);

        var exception = Assert.ThrowsExactly<Exception>(() => Schema.BindCommandKeys(typeof(WrongKeyCommand), target, payload));

        await Assert.That(exception.Message).Contains("keyed by 'Id', but carries no 'int' property named 'Id' or 'BadgeId'");
    }

    [Test]
    public async Task RefusesAResultFromAnotherAssembly()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.ResultProperties(typeof(MixedCommand), typeof(Uri), typeof(LockstepTests).Assembly));

        await Assert.That(exception.Message).Contains("answers with 'Uri', which is declared in assembly");
    }

    // The base's members are the derived type's own on both sides; the override is described once,
    // the indexer never, and an array is a collection of its element.
    [Test]
    public async Task AnUnannotatedBaseContributesItsMembersOnce()
    {
        var invoice = SharedProcessor.Instance.Describe().Types.Single(_ => _.Model == "InvoiceQueryModel");

        // AuditTrail is hidden on the base and stays hidden through the override; Reviewer is marked
        // on the base and stays marked through it. Both sides describe both that way.
        string[] expected = ["CreatedBy", "Id", "Notes", "Number", "Reviewer", "Tags", "Weights"];
        using (Assert.Multiple())
        {
            await Assert.That(invoice.Base).IsNull();
            await Assert.That(invoice.Members.Select(_ => _.Name)).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(invoice.Members.Single(_ => _.Name == "Reviewer").IsSensitive).IsTrue();
            await Assert.That(invoice.Members.Single(_ => _.Name == "Tags").TypeDisplay).IsEqualTo("global::System.Collections.Generic.IReadOnlyList<string>");
        }
    }

    // What the server refuses because the generator could not read it. Plain fixture types, opted
    // into nothing, so they poison no schema built over this assembly.
    // Built at runtime rather than declared here: a type carrying an opt-in attribute in this assembly
    // would be swept into every schema built over it, and this one is refused on sight.
    [Test]
    public async Task RefusesATypeOptedInTwice()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new("OptedInTwice"), AssemblyBuilderAccess.Run);
        var builder = assembly.DefineDynamicModule("OptedInTwice").DefineType("Both", TypeAttributes.Public);
        builder.SetCustomAttribute(new(typeof(QueryableAttribute).GetConstructor([])!, []));
        builder.SetCustomAttribute(new(typeof(QueryableComplexAttribute).GetConstructor([])!, []));
        var type = builder.CreateType();

        var exception = Assert.ThrowsExactly<Exception>(() => Schema.EnsureOneOptIn(type));

        await Assert.That(exception.Message).Contains("'Both' carries [Queryable] and [QueryableComplex]");
    }

    [Test]
    public async Task RefusesAMemberInheritedFromAnotherAssembly()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.BuildTypeMeta(typeof(ForeignBaseRow), []));

        await Assert.That(exception.Message).Contains("inherited from 'List`1'");
    }

    [Test]
    public async Task RefusesAnEnumFromAnotherAssembly()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.BuildTypeMeta(typeof(ForeignEnumRow), []));

        await Assert.That(exception.Message).IsEqualTo("'ForeignEnumRow.Comparison' is a 'StringComparison', an enum declared in assembly 'System.Private.CoreLib'. A client re-emits an enum from the model assembly's metadata alone, so it could never see this one, and every client would report itself stale. Declare the enum in the model assembly, or exclude the member with [QueryIgnore].");
    }

    // The BCL allow-list is closed: an enum of the BCL that is not on it is refused like any other
    // foreign enum, as a collection's element as much as a member's own type.
    [Test]
    public async Task RefusesACollectionOfAnEnumFromAnotherAssembly()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.BuildTypeMeta(typeof(ForeignEnumCollectionRow), []));

        await Assert.That(exception.Message).IsEqualTo("'ForeignEnumCollectionRow.Comparisons' is a 'StringComparison', an enum declared in assembly 'System.Private.CoreLib'. A client re-emits an enum from the model assembly's metadata alone, so it could never see this one, and every client would report itself stale. Declare the enum in the model assembly, or exclude the member with [QueryIgnore].");
    }

    [Test]
    public async Task RefusesACollectionShapeTheGeneratorDoesNotRead()
    {
        var exception = Assert.ThrowsExactly<Exception>(() => Schema.BuildTypeMeta(typeof(OddCollectionRow), []));

        await Assert.That(exception.Message).Contains("a collection shape the generator does not read");
    }

    [Test]
    public async Task ExposableCollectionShapesAreTheGeneratorsList()
    {
        using (Assert.Multiple())
        {
            await Assert.That(Schema.ExposableCollectionElement(typeof(string[]))).IsEqualTo(typeof(string));
            await Assert.That(Schema.ExposableCollectionElement(typeof(List<int>))).IsEqualTo(typeof(int));
            await Assert.That(Schema.ExposableCollectionElement(typeof(IReadOnlyList<Guid>))).IsEqualTo(typeof(Guid));
            await Assert.That(Schema.ExposableCollectionElement(typeof(int[,]))).IsNull();
            await Assert.That(Schema.ExposableCollectionElement(typeof(SortedSet<int>))).IsNull();
            await Assert.That(Schema.ExposableCollectionElement(typeof(string))).IsNull();
        }
    }

    // ReSharper disable UnusedMember.Local
    class ForeignBaseRow :
        List<int>;

    class ForeignEnumRow
    {
        public StringComparison Comparison { get; set; }
    }

    class ForeignEnumCollectionRow
    {
        [QueryableCollection]
        public List<StringComparison> Comparisons { get; set; } = [];
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
        public StringComparison Comparison { get; set; }
    }

    class BclEnumCommand
    {
        public DayOfWeek Day { get; set; }
        public DayOfWeek? Recess { get; set; }
        public DateTimeKind Clock { get; set; }
        public List<DayOfWeek> Alternates { get; set; } = [];
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
