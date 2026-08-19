/// <summary>
/// The keys each cached policy answered with during one call, so every site that applies the same
/// policy reads the same set. A query's root, a join's inner side and a traversal are all reading one
/// query's rows; a set that moved between them would be two answers to one question.
/// </summary>
sealed class CachedDecisions
{
    readonly Dictionary<CachedPolicyRegistration, object> answers = [];

    public object? Get(CachedPolicyRegistration registration) =>
        answers.GetValueOrDefault(registration);

    public void Set(CachedPolicyRegistration registration, object allowed) =>
        answers[registration] = allowed;
}
