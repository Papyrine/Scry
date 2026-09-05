/// <summary>
/// What each cached row policy answered with during one call, and the work it put off. One per
/// call, so the several sites that can apply the same policy to one query all read the same keys.
/// </summary>
sealed class CachedDecisions
{
    readonly Dictionary<CachedPolicyRegistration, object> answers = [];
    readonly List<IPendingDecision> pending = [];

    public object? Get(CachedPolicyRegistration registration) =>
        answers.GetValueOrDefault(registration);

    public void Set(CachedPolicyRegistration registration, object allowed) =>
        answers[registration] = allowed;

    /// <summary>
    /// Puts off bringing a scope up to date until the executor is ready to ask the database — after
    /// the query is built and before it runs — so the question can be asked the way the executor
    /// asks everything else, awaited rather than blocked on.
    /// </summary>
    public void Defer(IPendingDecision decision) =>
        pending.Add(decision);

    public void Refresh()
    {
        foreach (var decision in pending)
        {
            decision.Run();
        }

        pending.Clear();
    }

    public async ValueTask RefreshAsync(Cancel cancel)
    {
        foreach (var decision in pending)
        {
            await decision.RunAsync(cancel);
        }

        pending.Clear();
    }
}

/// <summary>
/// A cached policy's refresh, deferred from where the policy was applied to where the executor asks
/// the database. Fills the parameter the policy's membership test already binds.
/// </summary>
interface IPendingDecision
{
    void Run();

    ValueTask RunAsync(Cancel cancel);
}
