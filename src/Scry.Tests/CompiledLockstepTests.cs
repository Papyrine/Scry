/// <summary>
/// The generator and the server over models compiled per case (<see cref="CompiledModel"/>): shapes kept
/// out of this assembly's own model — bases the model declares generic, overrides through opted-in bases
/// and members hidden with <c>new</c>, and overrides of members another assembly declares
/// (<c>ForeignBases</c>). Each runs both readers over the same assembly and compares as
/// <see cref="LockstepTests"/> does: the stamp, then member by member and command by command.
/// </summary>
public class CompiledLockstepTests
{
    // Invoice's Id is an int and Customer's a Guid?, a parameter passed on through a second generic
    // level arrives as the argument it was given, an enum argument reaches the description, and an
    // override of a virtual the base hides stays hidden.
    [Test]
    public Task AGenericBase() =>
        Agree(
            """
            public enum Stage { Draft, Issued, Paid }

            public abstract class Entity<TKey>
            {
                public TKey Id { get; set; } = default!;
                public virtual string Notes { get; set; } = "";
                [QueryIgnore] public virtual string Secret { get; set; } = "";
            }

            public abstract class Tracked<TKey, TStage> : Entity<TKey>
                where TKey : struct
            {
                public TStage Stage { get; set; } = default!;
                public TKey? ParentId { get; set; }
                [QueryableCollection] public List<TKey> Related { get; set; } = [];
                [QueryableCollection] public List<TStage> History { get; set; } = [];
            }

            [Queryable]
            public class Invoice : Tracked<int, Stage>
            {
                public string Number { get; set; } = "";
                public override string Notes { get; set; } = "";
                public override string Secret { get; set; } = "";
                public Customer? Customer { get; set; }
            }

            [Queryable]
            public class Customer : Entity<Guid?>
            {
                public string Name { get; set; } = "";
            }
            """);

    [Test]
    public Task AGenericBaseBetweenOptedInTypes() =>
        Agree(
            """
            [Queryable]
            public class Asset
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public abstract class Tracked<TStamp> : Asset
            {
                public TStamp Stamp { get; set; } = default!;
            }

            [Queryable]
            public class Vehicle : Tracked<long>
            {
                public int Wheels { get; set; }
            }
            """);

    [Test]
    public Task AGenericBaseOfACommandAndItsResult() =>
        Agree(
            """
            [Queryable]
            public class Document
            {
                public int Id { get; set; }
                public string Title { get; set; } = "";
            }

            public abstract class Targeted<TKey>
            {
                public TKey Id { get; set; } = default!;
                [CommandIgnore] public string By { get; set; } = "";
            }

            public abstract class Envelope<TKey>
            {
                public TKey Id { get; set; } = default!;
            }

            public class Renamed : Envelope<int>
            {
                public string Title { get; set; } = "";
            }

            [Command(typeof(Document), Result = typeof(Renamed))]
            public class Rename : Targeted<int>
            {
                public string Title { get; set; } = "";
            }
            """);

    // What the server would otherwise refuse: each override repeats the marking its foreign
    // declaration carries, so the generator reads it too.
    [Test]
    public Task OverridesThatRepeatAForeignMarking() =>
        Agree(
            """
            [Queryable]
            public class Row : ForeignIgnoredBase
            {
                public int Id { get; set; }
                [QueryIgnore] public override string Secret { get; set; } = "";
            }

            [Queryable]
            public class Payroll : ForeignSensitiveBase
            {
                public int Id { get; set; }
                [Sensitive] public override string Salary { get; set; } = "";
            }

            [Queryable]
            public class Labelled : ForeignCollectionBase
            {
                public int Id { get; set; }
                [QueryableCollection] public override List<string> Tags { get; set; } = [];
            }

            [Command]
            public class Stamp : ForeignCommandIgnoredBase
            {
                public int Id { get; set; }
                [CommandIgnore] public override string By { get; set; } = "";
            }
            """);

    // A foreign member hidden by its own declaration is hidden on both sides with nothing to repeat:
    // the server reads the marking where it is written, and the generator never reads the member.
    [Test]
    public Task AForeignMemberHiddenWhereItIsDeclared() =>
        Agree(
            """
            [Queryable]
            public class Plain : ForeignPlainBase
            {
                public int Id { get; set; }
            }
            """);

    // The fix the server names for a member another assembly declares: override it with [QueryIgnore].
    // The declaration it hides is that assembly's, so what it carries is neither the author's to remove
    // nor anything the generator reads, and hiding the member is not refused over it.
    [Test]
    public Task ForeignMembersHiddenByOverridingThem() =>
        Agree(
            """
            [Queryable]
            public class Row : ForeignAttachmentBase
            {
                public int Id { get; set; }
                [QueryIgnore] public override byte[]? Document { get; set; }
            }

            [Queryable]
            public class Blobbed : ForeignBinaryBase
            {
                public int Id { get; set; }
                [QueryIgnore] public override byte[]? Blob { get; set; }
            }

            [Queryable]
            public class Renamed : ForeignRenamedBase
            {
                public int Id { get; set; }
                [QueryIgnore] public override string Current { get; set; } = "";
            }
            """);

    // An override below an opted-in base carries that base's declaration's attributes, as reflection
    // reads them: Secret stays hidden on Vehicle, and Title stays deprecated, though the generated
    // Vehicle model declares neither and inherits Asset's.
    [Test]
    public Task AnOverrideBelowAnOptedInBase() =>
        Agree(
            """
            [Queryable]
            public class Asset
            {
                public int Id { get; set; }
                [QueryIgnore] public virtual string Secret { get; set; } = "";
                [Obsolete("Use Name.")] public virtual string Title { get; set; } = "";
                public string Name { get; set; } = "";
            }

            [Queryable]
            public class Vehicle : Asset
            {
                public override string Secret { get; set; } = "";
                public override string Title { get; set; } = "";
                public int Wheels { get; set; }
            }
            """);

    // An override of a deprecated member is deprecated: C# binds a use of it to the declaration it
    // overrides. Both sides read it along the chain, nearest declaration first.
    [Test]
    public Task AnOverrideOfADeprecatedMember() =>
        Agree(
            """
            public abstract class Labelled
            {
                [Obsolete("Use Name.")] public virtual string Title { get; set; } = "";
                [Obsolete("Old.")] public virtual string Code { get; set; } = "";
            }

            [Queryable]
            public class Row : Labelled
            {
                public int Id { get; set; }
                public override string Title { get; set; } = "";
                [Obsolete("Newer.")] public override string Code { get; set; } = "";
            }
            """);

    // A member hiding another with 'new', virtual or not, starts again: reflection reads none of what
    // it hides, so neither may the generator — Secret, Code and Label are the derived type's, exposed
    // and current, and so is By on the command.
    [Test]
    public Task AMemberHidingAnotherWithNew() =>
        Agree(
            """
            public abstract class Hidden
            {
                [QueryIgnore] public string Secret { get; set; } = "";
                [QueryIgnore] public virtual string Code { get; set; } = "";
                [Obsolete("Old.")] public string Label { get; set; } = "";
            }

            [Queryable]
            public class Row : Hidden
            {
                public int Id { get; set; }
                public new string Secret { get; set; } = "";
                public new virtual string Code { get; set; } = "";
                public new string Label { get; set; } = "";
            }

            public abstract class Stamped
            {
                [CommandIgnore] public string By { get; set; } = "";
            }

            [Command]
            public class Note : Stamped
            {
                public int Id { get; set; }
                public new string By { get; set; } = "";
            }
            """);

    // EF reads [Keyless] through inheritance, so a type deriving from a keyless one is keyless, and a
    // view on both sides.
    [Test]
    public Task ATypeDerivingFromAKeylessOne() =>
        Agree(
            """
            [Keyless]
            public abstract class Report
            {
                public int Total { get; set; }
            }

            [Queryable]
            public class Monthly : Report
            {
                public int Month { get; set; }
            }
            """);

    static async Task Agree(string model)
    {
        var (assembly, image) = CompiledModel.Compile(
            $$"""
              {{model}}

              public sealed class Context : DbContext;
              """);
        var options = new ScryOptions(assembly.GetType("Context")!);
        var schema = Schema.Build(options);

        // The generator reads a model from disk, never from a loaded assembly.
        var path = Path.Combine(Path.GetTempPath(), $"{assembly.GetName().Name}.dll");
        await File.WriteAllBytesAsync(path, image);
        try
        {
            var extract = MetadataModelReader.Read(path);
            var described = schema.Describe(options);

            using (Assert.Multiple())
            {
                await Assert.That(extract.Error).IsNull();
                await Assert.That(ScryGenerator.ComputeStamp(extract)).IsEqualTo(schema.Stamp);
            }

            await LockstepTests.AgreeOnEveryMember(extract, described);
            await LockstepTests.AgreeOnEveryCommand(extract, described);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
