/// <summary>
/// Registering the client over a named <see cref="HttpClient"/>, which is what an application that is
/// not Blazor WebAssembly should do: the ambient registration the parameterless overload picks up may
/// belong to another API, and a bare HttpClient in the container is discouraged there anyway.
/// </summary>
[TestFixture]
public class ClientRegistrationTests
{
    record NameRow(string Name);

    [Test]
    public async Task ResolvesTheClientFromANamedHttpClient()
    {
        var server = await SharedScryServer.InstanceAsync();
        var services = new ServiceCollection();

        // begin-snippet: clientNamedRegistration
        var scry = services.AddHttpClient("scry", _ => _.BaseAddress = new("http://localhost/"));
        services.AddScryClient(
            "/api/query",
            _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
        services.AddScoped<ScryQuery>();
        // end-snippet

        // Points the named client at the in-process server instead of a socket.
        scry.ConfigurePrimaryHttpMessageHandler(server.CreateHandler);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var query = scope.ServiceProvider.GetRequiredService<ScryQuery>();
        var rows = await query.Employee
            .OrderBy(_ => _.Name)
            .Select(_ => new NameRow(_.Name))
            .ToListAsync();

        Assert.That(rows.Select(_ => _.Name), Is.Not.Empty);
    }

    [Test]
    public async Task KeepsOneClientPerScopeSoDriftIsStillDetected()
    {
        var server = await SharedScryServer.InstanceAsync();
        var services = new ServiceCollection();

        var scry = services.AddHttpClient("scry", _ => _.BaseAddress = new("http://localhost/"));
        services.AddScryClient(
            "/api/query",
            _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
        scry.ConfigurePrimaryHttpMessageHandler(server.CreateHandler);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Scoped, not transient: the client records the stamp each response advertises and reports
        // drift at most once, which a fresh instance per injection would reset.
        var first = scope.ServiceProvider.GetRequiredService<ScryClient>();
        var second = scope.ServiceProvider.GetRequiredService<ScryClient>();

        Assert.That(first, Is.SameAs(second));

        await new ScryQuery(first).Employee.Select(_ => new NameRow(_.Name)).ToListAsync();

        Assert.That(second.ServerSchemaStamp, Is.Not.Null);
    }
}
