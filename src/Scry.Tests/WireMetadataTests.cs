/// <summary>
/// Guards the claim <c>WireJsonContext</c> makes: that the whole wire vocabulary is source-generated
/// and none of it reaches the reflection resolver. Nothing breaks if one does — the fallback reads the
/// same attributes and produces the same JSON — so the drift is silent, and this is what makes it not.
/// </summary>
[TestFixture]
public class WireMetadataTests
{
    // The generated resolver, which sits ahead of the reflection fallback covering payload types.
    static readonly IJsonTypeInfoResolver generated = ScryJson.Options.TypeInfoResolverChain[0];

    [Test]
    public void GeneratedMetadataIsAheadOfReflection()
    {
        // Pins the ordering the two sweeps below rely on. A row is the shape a payload arrives in and
        // is nothing the wire assembly declares, so the generated resolver cannot answer it — if it
        // did, the resolver under test would be the reflection one and the sweeps would pass vacuously.
        Assert.That(generated.GetTypeInfo(typeof(Dictionary<string, object?>), ScryJson.Options), Is.Null);
        Assert.That(ScryJson.Options.GetTypeInfo(typeof(Dictionary<string, object?>)), Is.Not.Null);
    }

    [Test]
    public void EveryWireTypeIsSourceGenerated()
    {
        var reflected = WireTypes()
            .Where(_ => generated.GetTypeInfo(_, ScryJson.Options) is null)
            .Select(_ => _.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.That(
            reflected,
            Is.Empty,
            "These wire types fall back to reflection. Add a [JsonSerializable] root to WireJsonContext that reaches them.");
    }

    [Test]
    public void EveryPolymorphicCaseIsSourceGenerated()
    {
        // Listed separately from the sweep above because a derived type is reached by the generator
        // through the discriminator map rather than through a property, and that is exactly the
        // traversal a new operator or node depends on.
        var cases = new[] { typeof(QueryOp), typeof(Node), typeof(ProjectionValue) }
            .SelectMany(_ => _.GetCustomAttributes<JsonDerivedTypeAttribute>())
            .Select(_ => _.DerivedType)
            .ToList();

        Assert.That(cases, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var type in cases)
            {
                Assert.That(generated.GetTypeInfo(type, ScryJson.Options), Is.Not.Null, type.Name);
            }
        });
    }

    // Every record the wire assembly declares. Open generics are excluded: ScryPage<T> is closed over
    // the consumer's own row type, which is the one thing that cannot be generated ahead of time.
    static IEnumerable<Type> WireTypes() =>
        typeof(QueryRequest)
            .Assembly
            .GetTypes()
            .Where(_ => _ is {IsPublic: true, IsGenericTypeDefinition: false} &&
                        _.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public) is not null);
}
