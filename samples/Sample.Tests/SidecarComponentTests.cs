using Bunit;

/// <summary>
/// The sidecar component's contextual toggle button: shown by default, absent under
/// <see cref="ScrySidecarOptions.Never"/>, and decidable by a predicate over the app's services —
/// which is how an app keys it off the current user.
/// </summary>
[TestFixture]
public class SidecarComponentTests
{
    [Test]
    public async Task ToggleButtonShowsByDefault()
    {
        await using var context = Context(new());

        var component = context.Render<ScrySidecar>();
        await component.WaitForStateAsync(
            () => component.FindAll("[data-testid=sidecar-toggle]").Count == 1,
            TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task NeverHidesTheToggleButton()
    {
        await using var context = Context(new()
        {
            ToggleButton = ScrySidecarOptions.Never
        });

        var component = context.Render<ScrySidecar>();

        // The predicate runs after the first render; give it that pass before asserting absence.
        await Task.Delay(50);
        component.Render();
        Assert.That(component.FindAll("[data-testid=sidecar-toggle]"), Is.Empty);
    }

    // The predicate receives the app's services, so a decision from the current context — here a
    // stand-in for reading the signed-in user — reaches the button.
    [Test]
    public async Task PredicateDecidesFromTheCurrentContext()
    {
        var options = new ScrySidecarOptions
        {
            ToggleButton = async services =>
            {
                var user = services.GetRequiredService<FakeCurrentUser>();
                await Task.Yield();
                return user.IsDeveloper;
            }
        };
        await using var context = Context(options);
        context.Services.AddSingleton(new FakeCurrentUser(IsDeveloper: true));

        var component = context.Render<ScrySidecar>();
        await component.WaitForStateAsync(
            () => component.FindAll("[data-testid=sidecar-toggle]").Count == 1,
            TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task PredicateHidesFromTheCurrentContext()
    {
        var options = new ScrySidecarOptions
        {
            ToggleButton = services =>
                ValueTask.FromResult(services.GetRequiredService<FakeCurrentUser>().IsDeveloper)
        };
        await using var context = Context(options);
        context.Services.AddSingleton(new FakeCurrentUser(IsDeveloper: false));

        var component = context.Render<ScrySidecar>();

        await Task.Delay(50);
        component.Render();
        Assert.That(component.FindAll("[data-testid=sidecar-toggle]"), Is.Empty);
    }

    record FakeCurrentUser(bool IsDeveloper);

    static BunitContext Context(ScrySidecarOptions options)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(options);
        context.Services.AddSingleton(new ScrySidecarStore(options));
        return context;
    }
}
