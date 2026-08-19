/// <summary>
/// The operators a query applied to its root that decide which rows it reads, recorded as the pipeline
/// is folded so the denied-row probe can rebuild the same rows over a differently-policied root.
/// </summary>
/// <remarks>
/// Recorded rather than re-derived because the fold executes as it goes — a page materializes inside
/// it, a terminal runs at the end of it — so there is no second walk to make, and a walker written to
/// make one would have to stay in lockstep with this one forever.
/// </remarks>
sealed class ProbeSteps
{
    readonly List<ProbeStep> steps = [];

    /// <summary>
    /// Whether recording has stopped. Set at the first operator whose rows are no longer the root's —
    /// a page or a flattening — after which nothing describes which root rows were read, and the probe
    /// asks about the wider set the operators before it selected.
    /// </summary>
    public bool Stopped { get; private set; }

    public IReadOnlyList<ProbeStep> Recorded => steps;

    public void Where(LambdaExpression predicate)
    {
        if (!Stopped)
        {
            steps.Add(new(predicate, null, 0));
        }
    }

    public void Narrow(ScrySource derived, int from)
    {
        if (!Stopped)
        {
            steps.Add(new(null, derived, from));
        }
    }

    public void Stop() =>
        Stopped = true;

    /// <summary>The sources whose policies the recorded rows passed through, the root included.</summary>
    public IEnumerable<ScrySource> Sources(ScrySource root)
    {
        yield return root;
        foreach (var step in steps)
        {
            if (step.Narrow is { } derived)
            {
                yield return derived;
            }
        }
    }
}

/// <summary>One recorded operator: a predicate over the current element type, or a narrowing to a subclass.</summary>
readonly record struct ProbeStep(LambdaExpression? Where, ScrySource? Narrow, int NarrowFrom);
