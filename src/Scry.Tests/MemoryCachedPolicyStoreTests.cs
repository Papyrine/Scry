/// <summary>
/// The shipped store, driven the way a round of deciding drives it: read, decide for a while, apply.
/// The host can invalidate rows or forget the scope during the deciding, and what the store does
/// with the round then is the contract every store has to keep — a decision made under grants the
/// host has since revoked must not stand as the answer.
/// </summary>
[TestFixture]
public class MemoryCachedPolicyStoreTests
{
    const string policy = "RegionPolicy";
    const string scope = "tenant-1";

    [Test]
    public void ARoundAgainstTheCurrentGenerationResolvesWhatItDecided()
    {
        var store = new MemoryCachedPolicyStore();
        store.Apply(policy, scope, new([(1, true), (2, false)], 2, []));
        store.InvalidateRows(policy, [1, 1]);

        var read = store.Get(policy, scope)!;
        store.Apply(policy, scope, new([(1, false)], 2, read.PendingKeys) {Generation = read.Generation});

        var scoped = store.Get(policy, scope)!;
        Assert.Multiple(() =>
        {
            // The two invalidations of one key pend it once.
            Assert.That(read.PendingKeys, Is.EqualTo([1]));
            Assert.That(scoped.PendingKeys, Is.Empty);
            Assert.That(scoped.AllowedKeys, Is.Empty);
            Assert.That(scoped.Watermark, Is.EqualTo(2));
        });
    }

    [Test]
    public void ARowInvalidatedWhileARoundDecidesStaysPending()
    {
        var store = new MemoryCachedPolicyStore();
        store.Apply(policy, scope, new([(1, true)], 1, []));
        store.InvalidateRows(policy, [1]);
        var read = store.Get(policy, scope)!;

        // The round decides row 1 under the grants of the moment — allowed — and, before it applies,
        // the host revokes the grant and says so. The decision it made is stale, and the store cannot
        // know that; what it can know is that the host re-pended a key this round claims to resolve.
        store.InvalidateRows(policy, [1]);
        store.Apply(policy, scope, new([(1, true)], 1, read.PendingKeys) {Generation = read.Generation});

        var scoped = store.Get(policy, scope)!;
        Assert.Multiple(() =>
        {
            Assert.That(scoped.PendingKeys, Is.EqualTo([1]));
            Assert.That(scoped.Generation, Is.Not.EqualTo(read.Generation));
        });
    }

    [Test]
    public void AScopeForgottenWhileARoundDecidesDropsTheRound()
    {
        var store = new MemoryCachedPolicyStore();
        store.Apply(policy, scope, new([(1, true), (2, true)], 2, []));
        var read = store.Get(policy, scope)!;

        // The host forgets the scope while the round is deciding row 3. Seeding the forgotten scope
        // with that one decision and its watermark would leave rows 1 and 2 below the watermark and
        // never decided again — hidden from this caller for good.
        store.InvalidateScope(policy, scope);
        store.Apply(policy, scope, new([(3, true)], 3, []) {Generation = read.Generation});

        var scoped = store.Get(policy, scope)!;
        Assert.Multiple(() =>
        {
            Assert.That(scoped.AllowedKeys, Is.Empty);
            Assert.That(scoped.Watermark, Is.Null);
            Assert.That(scoped.PendingKeys, Is.Empty);
        });
    }

    [Test]
    public void AForgottenScopeIsDecidedAgainFromNothing()
    {
        var store = new MemoryCachedPolicyStore();
        store.Apply(policy, scope, new([(1, true)], 1, []));
        store.InvalidateScope(policy, scope);

        // The next round reads the forgotten scope — nothing known — and applies against what it
        // read, which is the current state; its decisions stand.
        var read = store.Get(policy, scope)!;
        store.Apply(policy, scope, new([(1, false), (2, true)], 2, []) {Generation = read.Generation});

        var scoped = store.Get(policy, scope)!;
        Assert.Multiple(() =>
        {
            Assert.That(read.Watermark, Is.Null);
            Assert.That(scoped.AllowedKeys, Is.EqualTo([2]));
            Assert.That(scoped.Watermark, Is.EqualTo(2));
        });
    }

    [Test]
    public void OnlyAnInvalidationMovesTheGeneration()
    {
        var store = new MemoryCachedPolicyStore();
        store.Apply(policy, scope, new([(1, true)], 1, []));
        var first = store.Get(policy, scope)!.Generation;
        store.Apply(policy, scope, new([(2, true)], 2, []) {Generation = first});
        var second = store.Get(policy, scope)!.Generation;
        store.InvalidateRows(policy, [2]);
        var third = store.Get(policy, scope)!.Generation;

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(third, Is.GreaterThan(second));
        });
    }
}
