/// <summary>
/// The server side of the claim check: what <see cref="ScryProcessor.FetchAttachment(AttachmentRequest, DbContext, IServiceProvider)"/>
/// hands over, and what it refuses. The HTTP shape those answers become — 200, 204, 404, 400 — is
/// pinned by the integration tests; this is about the decision, not the transport.
/// </summary>
[TestFixture]
public class AttachmentFetchTests
{
    static ScryAttachmentResult Fetch(int id, string member = "Document")
    {
        using var data = TestContext.CreateSeeded();
        return SharedProcessor.Instance.FetchAttachment(
            AttachmentRequest.Create("Contract", member, [new(id.ToString(), ClrTypeTag.Int32)]),
            data);
    }

    [Test]
    public void FetchesTheBytes()
    {
        var result = Fetch(1);

        Assert.Multiple(() =>
        {
            Assert.That(result.Found, Is.True);
            Assert.That(result.Value, Is.EqualTo(new byte[] {0x11, 0x22, 0x33}));
        });
    }

    // What the member declared, carried back on the result so a transport of its own serves the same
    // type the HTTP endpoint does.
    [Test]
    public void CarriesTheDeclaredContentType() =>
        Assert.That(Fetch(1).ContentType, Is.EqualTo("application/pdf"));

    // A row that is there holding a value that is not. Distinct from the refusals below: the caller
    // may read it, and what it reads is nothing.
    [Test]
    public void NullValueIsFoundWithNoBytes()
    {
        var result = Fetch(2);

        Assert.Multiple(() =>
        {
            Assert.That(result.Found, Is.True);
            Assert.That(result.Value, Is.Null);
        });
    }

    // A policy may replace the declared type for one fetch — the hook for a column holding more
    // than one kind of thing — and what it sets is what the result carries.
    [Test]
    public void APolicyMayRelabelTheBytes()
    {
        using var data = TestContext.CreateSeeded();

        var result = With<RelabellingPolicy>().FetchAttachment(Request(1), data);

        Assert.That(result.ContentType, Is.EqualTo("image/png"));
    }

    // The model's declaration is checked at startup; a policy's replacement can only be checked when
    // it is made. Host code, so a fault rather than a rejection — but a fault naming the policy,
    // never a response header carrying whatever was set.
    [Test]
    public void AReplacementThatIsNotAMediaTypeFaults()
    {
        using var data = TestContext.CreateSeeded();
        var processor = With<MislabellingPolicy>();

        var exception = Assert.Throws<Exception>(() => processor.FetchAttachment(Request(1), data))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception, Is.Not.InstanceOf<ScryValidationException>());
            Assert.That(exception.Message, Does.Contain("MislabellingPolicy"));
            Assert.That(exception.Message, Does.Contain("not a media type"));
        });
    }

    static AttachmentRequest Request(int id) =>
        AttachmentRequest.Create("Contract", "Document", [new(id.ToString(), ClrTypeTag.Int32)]);

    // The shared processor's model with the contract's attachment policy replaced, since the one
    // the model declares sets no type of its own.
    static ScryProcessor With<TPolicy>()
        where TPolicy : IAttachmentPolicy<Contract> =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AddAttachmentPolicy<Contract, TPolicy>();
        });

    public sealed class RelabellingPolicy :
        IAttachmentPolicy<Contract>
    {
        public bool Authorize(ScryAttachmentContext context)
        {
            context.ContentType = "image/png";
            return true;
        }
    }

    public sealed class MislabellingPolicy :
        IAttachmentPolicy<Contract>
    {
        public bool Authorize(ScryAttachmentContext context)
        {
            context.ContentType = "not a media type";
            return true;
        }
    }

    [Test]
    public void DeniedByPolicyIsNotFound() =>
        Assert.That(Fetch(UnsealedContractsPolicy.SealedId).Found, Is.False);

    [Test]
    public void MissingRowIsNotFound() =>
        Assert.That(Fetch(404).Found, Is.False);

    // The two answers a caller must not be able to tell apart: one row exists and is refused, the
    // other does not exist at all. Asserted together, since the guarantee is that they are equal.
    [Test]
    public void DeniedAndMissingAreIndistinguishable() =>
        Assert.That(Fetch(UnsealedContractsPolicy.SealedId), Is.EqualTo(Fetch(404)));

    [Test]
    public void UnknownMemberIsRejected()
    {
        var exception = Assert.Throws<ScryValidationException>(() => Fetch(1, "Ssn"));
        Assert.That(exception!.Message, Does.Contain("is not an attachment member"));
    }

    // A member that exists and is readable, but is not an attachment — the endpoint is not a way to
    // read an ordinary column.
    [Test]
    public void ScalarMemberIsRejected()
    {
        var exception = Assert.Throws<ScryValidationException>(() => Fetch(1, "Name"));
        Assert.That(exception!.Message, Does.Contain("is not an attachment member"));
    }

    [Test]
    public void UnknownSourceIsRejected()
    {
        using var data = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                AttachmentRequest.Create("Secret", "Document", [new("1", ClrTypeTag.Int32)]),
                data));

        Assert.That(exception!.Message, Does.Contain("Unknown source"));
    }

    [Test]
    public void WrongKeyCountIsRejected()
    {
        using var data = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                AttachmentRequest.Create("Contract", "Document", [new("1", ClrTypeTag.Int32), new("2", ClrTypeTag.Int32)]),
                data));

        Assert.That(exception!.Message, Does.Contain("keyed by 1 value"));
    }

    // The tag says Int32 and the value is not one. Rejected because the key is parsed into the
    // member's own type — the tag is a hint, and a value that does not parse is a malformed request
    // rather than a server fault.
    [Test]
    public void UnparseableKeyIsRejected()
    {
        using var data = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                AttachmentRequest.Create("Contract", "Document", [new("not-a-number", ClrTypeTag.Int32)]),
                data));

        Assert.That(exception!.Message, Does.Contain("not a valid Int32"));
    }

    // A primary key is never null, so a null key identifies no row. Answered as not-found rather than
    // rejected — it is a key that matches nothing, not a malformed one.
    [Test]
    public void NullKeyIsNotFound()
    {
        using var data = TestContext.CreateSeeded();
        var result = SharedProcessor.Instance.FetchAttachment(
            AttachmentRequest.Create("Contract", "Document", [new(null, ClrTypeTag.Null)]),
            data);

        Assert.That(result.Found, Is.False);
    }

    [Test]
    public void NewerVersionIsRejected()
    {
        using var data = TestContext.CreateSeeded();
        var exception = Assert.Throws<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                new(AttachmentRequest.CurrentVersion + 1, "Contract", "Document", [new("1", ClrTypeTag.Int32)]),
                data));

        Assert.That(exception!.Message, Does.Contain("Unsupported attachment request version"));
    }
}
