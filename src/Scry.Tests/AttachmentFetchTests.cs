/// <summary>
/// The server side of the claim check: what <see cref="ScryProcessor.FetchAttachment(AttachmentRequest, DbContext, IServiceProvider)"/>
/// hands over, and what it refuses. The HTTP shape those answers become — 200, 204, 404, 400 — is
/// pinned by the integration tests; this is about the decision, not the transport.
/// </summary>
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
    public async Task FetchesTheBytes()
    {
        var result = Fetch(1);

        using (Assert.Multiple())
        {
            await Assert.That(result.Found).IsTrue();
            await Assert.That(result.Value).IsEquivalentTo(new byte[] {0x11, 0x22, 0x33}, CollectionOrdering.Matching);
        }
    }

    // What the member declared, carried back on the result so a transport of its own serves the same
    // type the HTTP endpoint does.
    [Test]
    public async Task CarriesTheDeclaredContentType() =>
        await Assert.That(Fetch(1).ContentType).IsEqualTo("application/pdf");

    // A row that is there holding a value that is not. Distinct from the refusals below: the caller
    // may read it, and what it reads is nothing.
    [Test]
    public async Task NullValueIsFoundWithNoBytes()
    {
        var result = Fetch(2);

        using (Assert.Multiple())
        {
            await Assert.That(result.Found).IsTrue();
            await Assert.That(result.Value).IsNull();
        }
    }

    // A policy declared on the base applies to a derived source, as a row policy would: the derived
    // contract opts in with none of its own and is authorized by the base's.
    [Test]
    public async Task AnAttachmentPolicyOnTheBaseAppliesToTheDerivedSource()
    {
        // A database of its own: a signed contract in the shared seed would be one more contract in
        // every fixture that counts them.
        await using var database = await TestContext.CreateIsolated("SignedContract");
        await using (var writing = database.NewDbContext())
        {
            writing.Contracts.Add(new SignedContract
            {
                Id = 4,
                Name = "Signed lease",
                Signer = "Ada",
                Document = [0x44]
            });
            await writing.SaveChangesAsync();
        }

        await using var data = database.NewDbContext();
        var granted = SharedProcessor.Instance.FetchAttachment(
            AttachmentRequest.Create("SignedContract", "Document", [new("4", ClrTypeTag.Int32)]),
            data);
        var refused = SharedProcessor.Instance.FetchAttachment(
            AttachmentRequest.Create("SignedContract", "Document", [new(UnsealedContractsPolicy.SealedId.ToString(), ClrTypeTag.Int32)]),
            data);

        using (Assert.Multiple())
        {
            await Assert.That(granted.Found).IsTrue();
            await Assert.That(granted.Value).IsEquivalentTo(new byte[] {0x44}, CollectionOrdering.Matching);
            // The base's policy is the one answering: its sealed id is refused here too.
            await Assert.That(refused.Found).IsFalse();
        }
    }

    // The tag on a key value is the client's hint; the key is parsed as the member's own type, as a
    // constant in a predicate is.
    [Test]
    public async Task AKeyValueIsParsedAsTheKeysTypeWhateverItsTag()
    {
        await using var data = TestContext.CreateSeeded();

        var result = SharedProcessor.Instance.FetchAttachment(
            AttachmentRequest.Create("Contract", "Document", [new("1", ClrTypeTag.String)]),
            data);

        await Assert.That(result.Found).IsTrue();
    }

    // A policy may replace the declared type for one fetch — the hook for a column holding more
    // than one kind of thing — and what it sets is what the result carries.
    [Test]
    public async Task APolicyMayRelabelTheBytes()
    {
        await using var data = TestContext.CreateSeeded();

        var result = With<RelabellingPolicy>().FetchAttachment(Request(1), data);

        await Assert.That(result.ContentType).IsEqualTo("image/png");
    }

    // The model's declaration is checked at startup; a policy's replacement can only be checked when
    // it is made. Host code, so a fault rather than a rejection — but a fault naming the policy,
    // never a response header carrying whatever was set.
    [Test]
    public async Task AReplacementThatIsNotAMediaTypeFaults()
    {
        await using var data = TestContext.CreateSeeded();
        var processor = With<MislabellingPolicy>();

        var exception = Assert.ThrowsExactly<Exception>(() => processor.FetchAttachment(Request(1), data));

        using (Assert.Multiple())
        {
            await Assert.That(exception).IsNotAssignableTo<ScryValidationException>();
            await Assert.That(exception.Message).Contains("MislabellingPolicy");
            await Assert.That(exception.Message).Contains("not a media type");
        }
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
    public async Task DeniedByPolicyIsNotFound() =>
        await Assert.That(Fetch(UnsealedContractsPolicy.SealedId).Found).IsFalse();

    [Test]
    public async Task MissingRowIsNotFound() =>
        await Assert.That(Fetch(404).Found).IsFalse();

    // The two answers a caller must not be able to tell apart: one row exists and is refused, the
    // other does not exist at all. Asserted together, since the guarantee is that they are equal.
    [Test]
    public async Task DeniedAndMissingAreIndistinguishable() =>
        await Assert.That(Fetch(UnsealedContractsPolicy.SealedId)).IsEqualTo(Fetch(404));

    [Test]
    public async Task UnknownMemberIsRejected()
    {
        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Fetch(1, "Ssn"));
        await Assert.That(exception.Message).Contains("is not an attachment member");
    }

    // A member that exists and is readable, but is not an attachment — the endpoint is not a way to
    // read an ordinary column.
    [Test]
    public async Task ScalarMemberIsRejected()
    {
        var exception = Assert.ThrowsExactly<ScryValidationException>(() => Fetch(1, "Name"));
        await Assert.That(exception.Message).Contains("is not an attachment member");
    }

    [Test]
    public async Task UnknownSourceIsRejected()
    {
        await using var data = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                AttachmentRequest.Create("Secret", "Document", [new("1", ClrTypeTag.Int32)]),
                data));

        await Assert.That(exception.Message).Contains("Unknown source");
    }

    [Test]
    public async Task WrongKeyCountIsRejected()
    {
        await using var data = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                AttachmentRequest.Create("Contract", "Document", [new("1", ClrTypeTag.Int32), new("2", ClrTypeTag.Int32)]),
                data));

        await Assert.That(exception.Message).Contains("keyed by 1 value");
    }

    // The tag says Int32 and the value is not one. Rejected because the key is parsed into the
    // member's own type — the tag is a hint, and a value that does not parse is a malformed request
    // rather than a server fault.
    [Test]
    public async Task UnparseableKeyIsRejected()
    {
        await using var data = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                AttachmentRequest.Create("Contract", "Document", [new("not-a-number", ClrTypeTag.Int32)]),
                data));

        await Assert.That(exception.Message).Contains("not a valid Int32");
    }

    // A primary key is never null, so a null key identifies no row. Answered as not-found rather than
    // rejected — it is a key that matches nothing, not a malformed one.
    [Test]
    public async Task NullKeyIsNotFound()
    {
        await using var data = TestContext.CreateSeeded();
        var result = SharedProcessor.Instance.FetchAttachment(
            AttachmentRequest.Create("Contract", "Document", [new(null, ClrTypeTag.Null)]),
            data);

        await Assert.That(result.Found).IsFalse();
    }

    [Test]
    public async Task NewerVersionIsRejected()
    {
        await using var data = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                new(AttachmentRequest.CurrentVersion + 1, "Contract", "Document", [new("1", ClrTypeTag.Int32)]),
                data));

        await Assert.That(exception.Message).Contains("Unsupported attachment request version");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task AVersionBelowOneIsRejected(int version)
    {
        await using var data = TestContext.CreateSeeded();
        var exception = Assert.ThrowsExactly<ScryValidationException>(
            () => SharedProcessor.Instance.FetchAttachment(
                new(version, "Contract", "Document", [new("1", ClrTypeTag.Int32)]),
                data));

        await Assert.That(exception.Message).Contains("Unsupported attachment request version");
    }
}
