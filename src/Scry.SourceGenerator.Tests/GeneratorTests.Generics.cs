public partial class GeneratorTests
{
    // A base the model declares generic is read with the arguments the deriving type supplies, as the
    // server reads it by reflection: Invoice's Id is an int and Customer's a Guid?, a parameter passed on
    // through a second generic level arrives as the argument it was given, an enum argument is
    // re-emitted, and an override of a virtual the base hides stays hidden. Box is a generic type of the
    // model's own used as a member's type, which neither side exposes.
    [Test]
    public Task GenericBase()
    {
        const string model = """
            using System;
            using System.Collections.Generic;
            using Scry;

            namespace Sample.Model;

            public enum Stage { Draft, Issued, Paid }

            public class Box<T>
            {
                public T Value { get; set; } = default!;
            }

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
                public Box<int>? Box { get; set; }
            }

            [Queryable]
            public class Customer : Entity<Guid?>
            {
                public string Name { get; set; } = "";
            }
            """;

        return VerifyGenerated(model);
    }

    // A generic type between two opted-in ones is walked through, as the server links through it: the
    // derived model inherits the opted-in base's, and declares the generic level's members as its own.
    [Test]
    public Task GenericBaseBetweenOptedInTypes()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

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
            """;

        return VerifyGenerated(model);
    }

    // A command's payload and its result read a generic base the same way: Rename's key comes from its
    // base with the argument filled in, so it binds to Document's int key, the base's [CommandIgnore]
    // keeps By out of the payload, and Renamed's base member is part of the result.
    [Test]
    public Task GenericBaseOfACommandAndItsResult()
    {
        const string model = """
            using Scry;

            namespace Sample.Model;

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
            """;

        return VerifyGenerated(model);
    }

    // A generic base the model does not declare is not followed: its members are never read, and the
    // server refuses any it would otherwise expose.
    [Test]
    public Task AGenericBaseFromAnotherAssemblyIsNotRead()
    {
        const string model = """
            using System.Collections.ObjectModel;
            using Scry;

            namespace Sample.Model;

            [Queryable]
            public class Tags : Collection<string>
            {
                public int Id { get; set; }
            }
            """;

        return VerifyGenerated(model);
    }

    // Refused, as the server refuses it, rather than emitted as a query model named for a type whose
    // members a client could not name. A Name does not help: the type is still generic.
    [Test]
    [Arguments("[Queryable]", "[Queryable]")]
    [Arguments("[Queryable(Name = \"Entity\")]", "[Queryable]")]
    [Arguments("[QueryableView]", "[QueryableView]")]
    [Arguments("[QueryablePoco]", "[QueryablePoco]")]
    [Arguments("[QueryableComplex]", "[QueryableComplex]")]
    public async Task AGenericTypeOptedInIsRefused(string optIn, string reported)
    {
        var model = $$"""
            using Scry;

            namespace Sample.Model;

            {{optIn}}
            public class Entity<TKey>
            {
                public TKey Id { get; set; } = default!;
            }

            [Queryable]
            public class Fine
            {
                public int Id { get; set; }
            }
            """;

        var result = (await RunGenerator(model)).GetRunResult();

        var diagnostic = result.Diagnostics.Single(_ => _.Id == "SCRY018");
        using (Assert.Multiple())
        {
            await Assert.That(diagnostic.GetMessage()).StartsWith($"'Entity' carries {reported} but is generic.");
            await Assert.That(result.Results.SelectMany(_ => _.GeneratedSources)).IsEmpty();
        }
    }
}
