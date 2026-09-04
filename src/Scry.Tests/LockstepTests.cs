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

    // The base's members are the derived type's own on both sides; the override is described once,
    // the indexer never, and an array is a collection of its element.
    [Test]
    public void AnUnannotatedBaseContributesItsMembersOnce()
    {
        var invoice = SharedProcessor.Instance.Describe().Types.Single(_ => _.Model == "InvoiceQueryModel");

        string[] expected = ["CreatedBy", "Id", "Notes", "Number", "Tags", "Weights"];
        Assert.Multiple(() =>
        {
            Assert.That(invoice.Base, Is.Null);
            Assert.That(invoice.Members.Select(_ => _.Name), Is.EqualTo(expected));
            Assert.That(invoice.Members.Single(_ => _.Name == "Tags").TypeDisplay, Is.EqualTo("global::System.Collections.Generic.IReadOnlyList<string>"));
        });
    }

    // What the server refuses because the generator could not read it. Plain fixture types, opted
    // into nothing, so they poison no schema built over this assembly.
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
    // ReSharper restore UnusedMember.Local
}
