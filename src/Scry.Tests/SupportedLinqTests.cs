// The analyzer reports against SupportedLinq, and the translator enforces the same set by hand. The
// two are separate implementations of one rule set, which is exactly the pair that drifts: a function
// added to the wire and to QueryTranslator but not to the table turns the analyzer into something
// that rejects a query the client would have carried.
//
// So the table is pinned here — in the tree that can see both the wire and the framework — rather
// than only in the analyzer's own tests, which would pin it against itself.
[TestFixture]
public class SupportedLinqTests
{
    [Test]
    public void EveryWireFunctionIsNamedByTheTable()
    {
        var covered = SupportedLinq.Functions
            .Select(_ => _.Function)
            .Where(_ => _.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var missing = Enum.GetNames<KnownFunction>()
            .Where(_ => !covered.Contains(_))
            .ToList();

        Assert.That(
            missing,
            Is.Empty,
            () => $"KnownFunction has values the analyzer's table does not spell, so a query using them would be reported as unsupported: {string.Join(", ", missing)}");
    }

    [Test]
    public void EveryTableEntryNamesARealMember()
    {
        foreach (var (signature, _) in SupportedLinq.Functions)
        {
            var (owner, member, arity) = Parse(signature);

            // A marker rather than a member: a client-supplied set reaches the wire through Contains
            // over a closure collection, which is an Enumerable call and not a member of a scalar.
            if (owner == "$set")
            {
                continue;
            }

            Assert.That(
                Owners(owner).Any(_ => Exists(_, member, arity)),
                Is.True,
                () => $"'{signature}' names no member that exists.");
        }
    }

    [Test]
    public void EveryOperatorMatchesQueryable()
    {
        foreach (var (name, arities) in SupportedLinq.Operators)
        {
            var overloads = typeof(Queryable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(_ => _.Name == name)
                .Select(_ => _.GetParameters().Length)
                .ToHashSet();

            Assert.That(overloads, Is.Not.Empty, () => $"Queryable has no '{name}'.");
            foreach (var arity in arities)
            {
                Assert.That(
                    overloads,
                    Does.Contain(arity),
                    () => $"Queryable.{name} has no {arity}-argument overload, so the analyzer would report every call to it as an unsupported overload.");
            }
        }
    }

    // Every operator the analyzer allows at most once, and every one it counts as establishing an
    // ordering, has to be an operator it knows in the first place.
    [Test]
    public void CompositionRulesNameKnownOperators()
    {
        foreach (var name in SupportedLinq.SingleUse.Keys.Concat(SupportedLinq.Ordering))
        {
            Assert.That(SupportedLinq.Operators.ContainsKey(name), Is.True, () => $"'{name}' is not an operator.");
        }
    }

    // Kept in step with QueryTranslator.IsTemporal, which decides the same thing by CLR type.
    [Test]
    public void TemporalTypesResolve()
    {
        foreach (var name in SupportedLinq.Temporal)
        {
            Assert.That(Type.GetType(name), Is.Not.Null, () => $"'{name}' is not a type.");
        }
    }

    static (string Owner, string Member, int Arity) Parse(string signature)
    {
        var slash = signature.LastIndexOf('/');
        var path = signature[..slash];
        var dot = path.LastIndexOf('.');
        return (path[..dot], path[(dot + 1)..], int.Parse(signature[(slash + 1)..], CultureInfo.InvariantCulture));
    }

    // The four temporal types spell one shared set of members between them, and no one type has all
    // of it — a DateOnly has no Hour, a TimeOnly no Year. One of them declaring the member is what
    // makes the entry real.
    static IEnumerable<Type> Owners(string owner)
    {
        if (owner == SupportedLinq.TemporalOwner)
        {
            return SupportedLinq.Temporal.Select(_ => Type.GetType(_)!);
        }

        return [Type.GetType(owner)!];
    }

    static bool Exists(Type type, string member, int arity)
    {
        if (arity == 0 &&
            type.GetProperty(member) is not null)
        {
            return true;
        }

        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Any(_ => _.Name == member && _.GetParameters().Length == arity);
    }
}
