namespace Sample.Client;

/// <summary>
/// Watches for the server's queryable surface drifting away from the one this app was generated
/// against, and offers a reload when it does. Rendered once, above the router.
/// </summary>
public partial class StaleBanner :
    IDisposable
{
    SchemaDrift? drift;

    // begin-snippet: detectSchemaDrift
    protected override void OnInitialized() =>
        Client.SchemaStaleDetected += OnSchemaStale;

    // The server has been redeployed with a different query surface, so a newer client exists. The
    // app is still working — queries against the old surface keep succeeding — so show a prompt
    // rather than reloading out from under whatever the user is doing.
    void OnSchemaStale(SchemaDrift value)
    {
        drift = value;
        InvokeAsync(StateHasChanged);
    }

    // Bypasses the browser cache, so the reload fetches the newly published client.
    void Reload() =>
        Navigation.Refresh(forceReload: true);
    // end-snippet

    public void Dispose() =>
        Client.SchemaStaleDetected -= OnSchemaStale;
}
