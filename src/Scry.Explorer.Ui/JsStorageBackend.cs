using Microsoft.JSInterop;

/// <summary>
/// localStorage, reached through the <c>window.scry</c> helpers so quota failures come back as a
/// result rather than as an interop exception.
/// </summary>
/// <remarks>
/// The invokes are synchronous, which is valid because the classic script the helpers live in is
/// loaded before Blazor starts — that ordering is what lets the explorer rehydrate its state during
/// <c>OnInitialized</c> instead of a render later.
/// </remarks>
sealed class JsStorageBackend(IJSInProcessRuntime js) :
    IStorageBackend
{
    public string? Get(string key) =>
        js.Invoke<string?>("scry.storageGet", key);

    public bool Set(string key, string value)
    {
        var result = js.Invoke<string>("scry.storageSet", key, value);
        using var document = JsonDocument.Parse(result);
        return document.RootElement.GetProperty("ok").GetBoolean();
    }

    public void Remove(string key) =>
        js.InvokeVoid("scry.storageRemove", key);

    public IReadOnlyList<string> Keys() =>
        js.Invoke<string[]>("scry.storageKeys", "");
}
