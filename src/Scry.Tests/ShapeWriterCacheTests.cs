/// <summary>
/// The row writer is shared by shape rather than rebuilt per plan, which puts a client-supplied
/// projection in a cache key. Two shapes that are not the same must never resolve to one writer: a
/// collision would answer one projection in another's member order, and the projected names are the
/// caller's own text.
/// </summary>
[TestFixture]
public class ShapeWriterCacheTests
{
    [Test]
    public void SameShapeSharesOneWriter()
    {
        string[][] shape = [["Name"], ["Department", "Name"]];

        Assert.That(PlanShapeWriter.Get(shape, null), Is.SameAs(PlanShapeWriter.Get(shape, null)));
    }

    [Test]
    // Two requests of the same projection arrive as separate lists, so the cache has to match them by
    // content rather than by reference.
    public void EqualShapesBuiltSeparatelySharesOneWriter() =>
        Assert.That(
            PlanShapeWriter.Get([["Total"], ["Region"]], null),
            Is.SameAs(PlanShapeWriter.Get([["Total"], ["Region"]], null)));

    // The segment boundary a naive key would lose: "ab"+"c" and "a"+"bc" concatenate to the same text.
    [Test]
    public void ShapesDifferingOnlyInSegmentBoundariesDoNotShare() =>
        Assert.That(
            PlanShapeWriter.Get([["ab", "c"]], null),
            Is.Not.SameAs(PlanShapeWriter.Get([["a", "bc"]], null)));

    // The slot boundary, the same way: one two-segment path against two one-segment paths.
    [Test]
    public void ShapesDifferingOnlyInSlotBoundariesDoNotShare() =>
        Assert.That(
            PlanShapeWriter.Get([["a", "b"]], null),
            Is.Not.SameAs(PlanShapeWriter.Get([["a"], ["b"]], null)));

    // A name the caller chose that reads as the key's own punctuation.
    [Test]
    public void ShapesWhoseNamesLookLikeKeySeparatorsDoNotShare() =>
        Assert.That(
            PlanShapeWriter.Get([["1:a"]], null),
            Is.Not.SameAs(PlanShapeWriter.Get([["1", "a"]], null)));

    [Test]
    public void BinarySlotsArePartOfTheIdentity()
    {
        string[][] shape = [["Payload"]];

        Assert.That(PlanShapeWriter.Get(shape, null), Is.Not.SameAs(PlanShapeWriter.Get(shape, [true])));
        Assert.That(PlanShapeWriter.Get(shape, [false]), Is.Not.SameAs(PlanShapeWriter.Get(shape, [true])));
    }
}
