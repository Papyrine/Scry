/// <summary>
/// The row writer is shared by shape rather than rebuilt per plan, which puts a client-supplied
/// projection in a cache key. Two shapes that are not the same must never resolve to one writer: a
/// collision would answer one projection in another's member order, and the projected names are the
/// caller's own text.
/// </summary>
public class ShapeWriterCacheTests
{
    [Test]
    public async Task SameShapeSharesOneWriter()
    {
        string[][] shape = [["Name"], ["Department", "Name"]];

        await Assert.That(PlanShapeWriter.Get(shape, null)).IsSameReferenceAs(PlanShapeWriter.Get(shape, null));
    }

    [Test]
    // Two requests of the same projection arrive as separate lists, so the cache has to match them by
    // content rather than by reference.
    public async Task EqualShapesBuiltSeparatelySharesOneWriter() =>
        await Assert.That(PlanShapeWriter.Get([["Total"], ["Region"]], null)).IsSameReferenceAs(PlanShapeWriter.Get([["Total"], ["Region"]], null));

    // The segment boundary a naive key would lose: "ab"+"c" and "a"+"bc" concatenate to the same text.
    [Test]
    public async Task ShapesDifferingOnlyInSegmentBoundariesDoNotShare() =>
        await Assert.That(PlanShapeWriter.Get([["ab", "c"]], null)).IsNotSameReferenceAs(PlanShapeWriter.Get([["a", "bc"]], null));

    // The slot boundary, the same way: one two-segment path against two one-segment paths.
    [Test]
    public async Task ShapesDifferingOnlyInSlotBoundariesDoNotShare() =>
        await Assert.That(PlanShapeWriter.Get([["a", "b"]], null)).IsNotSameReferenceAs(PlanShapeWriter.Get([["a"], ["b"]], null));

    // A name the caller chose that reads as the key's own punctuation.
    [Test]
    public async Task ShapesWhoseNamesLookLikeKeySeparatorsDoNotShare() =>
        await Assert.That(PlanShapeWriter.Get([["1:a"]], null)).IsNotSameReferenceAs(PlanShapeWriter.Get([["1", "a"]], null));

    [Test]
    public async Task BinarySlotsArePartOfTheIdentity()
    {
        string[][] shape = [["Payload"]];

        await Assert.That(PlanShapeWriter.Get(shape, null)).IsNotSameReferenceAs(PlanShapeWriter.Get(shape, [true]));
        await Assert.That(PlanShapeWriter.Get(shape, [false])).IsNotSameReferenceAs(PlanShapeWriter.Get(shape, [true]));
    }

    // Slots built separately compare by value, as the paths do.
    [Test]
    public async Task EqualBinarySlotsBuiltSeparatelyShareOneWriter() =>
        await Assert.That(PlanShapeWriter.Get([["Payload"], ["Name"]], [true, false])).IsSameReferenceAs(PlanShapeWriter.Get([["Payload"], ["Name"]], [true, false]));
}
