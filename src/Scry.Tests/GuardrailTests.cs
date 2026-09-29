using Microsoft.EntityFrameworkCore;

public class GuardrailTests
{
    // Maps the same Address type as a (keyless) entity rather than a complex type. Validating the real
    // TestContext schema — where Address is [QueryableComplex] — against this model reproduces the
    // "[QueryableComplex] on a mapped entity" mix-up the startup guardrail exists to catch.
    sealed class AddressAsEntityContext(DbContextOptions<AddressAsEntityContext> options) :
        DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) =>
            builder.Entity<Address>().HasNoKey();
    }

    [Test]
    public async Task RejectsComplexTypeMappedAsEntity()
    {
        var options = new DbContextOptionsBuilder<AddressAsEntityContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ScryGuardrail")
            .Options;
        using var context = new AddressAsEntityContext(options);

        var exception = Assert.ThrowsExactly<Exception>(() => SharedProcessor.Instance.ValidateAgainstModel(context));
        await Assert.That(exception.Message).Contains("[QueryableComplex] but is a mapped entity");
    }
}
