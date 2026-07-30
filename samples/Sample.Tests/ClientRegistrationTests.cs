/// <summary>
/// The two ways to register the client. A named <see cref="HttpClient"/> is what an application that
/// is not Blazor WebAssembly should use: the ambient registration the parameterless overload picks up
/// may belong to another API, and a bare HttpClient in the container is discouraged there anyway.
/// </summary>
[TestFixture]
public class ClientRegistrationTests
{
    record NameRow(string Name);

    [Test]
    public async Task ResolvesTheClientFromTheAmbientHttpClient()
    {
        var services = new ServiceCollection();

        // The WebAssembly shortcut: one HttpClient, already pointed at the app's own origin, so there
        // is nothing for a name to disambiguate. In a real app the address comes from
        // builder.HostEnvironment.BaseAddress.
        // begin-snippet: clientWasmRegistration
        services.AddScoped(
            _ => new HttpClient
            {
                BaseAddress = new("https://localhost")
            });
        services.AddScryClient("/api/query");
        services.AddScoped<ScryQuery>();
        // end-snippet

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Resolving is the whole of what this overload does differently — it reaches the same
        // ScryClient the named form does, by a different route — so the round trip is covered by the
        // test below rather than repeated here.
        Assert.That(scope.ServiceProvider.GetRequiredService<ScryQuery>(), Is.Not.Null);
        Assert.That(
            scope.ServiceProvider.GetRequiredService<ScryClient>(),
            Is.SameAs(scope.ServiceProvider.GetRequiredService<ScryClient>()));
    }

    [Test]
    public async Task ResolvesTheClientFromANamedHttpClient()
    {
        var server = await SharedScryServer.InstanceAsync();
        var services = new ServiceCollection();

        var scry = services.AddHttpClient("scry", _ => _.BaseAddress = new("http://localhost/"));
        services.AddScryClient(
            "/api/query",
            _ => _.GetRequiredService<IHttpClientFactory>().CreateClient("scry"));
        services.AddScoped<ScryQuery>();

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
