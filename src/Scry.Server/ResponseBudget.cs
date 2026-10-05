/// <summary>
/// What one response may still carry under <see cref="ScryOptions.MaxResponseBytes"/>: its JSON and
/// its binary parts, counted as they are written rather than as they are resident — so a response that
/// drains as it goes is bounded by what it sent, not by what it is holding.
/// </summary>
/// <remarks>
/// The refusal is a <see cref="ScryValidationException"/>, as a live query's is, since what it says is
/// the client's to act on: ask for less. Refusing charges nothing, so a batch entry that would have
/// crossed the limit leaves it where it was for the entries after it.
/// </remarks>
sealed class ResponseBudget(int limit)
{
    long spent;

    /// <summary>A budget for one response, or null where the server sets no limit.</summary>
    public static ResponseBudget? For(ScryOptions options)
    {
        if (options.MaxResponseBytes is { } limit)
        {
            return new(limit);
        }

        return null;
    }

    /// <summary>Whether <paramref name="count"/> more bytes would still be inside the limit.</summary>
    public bool Fits(long count) =>
        spent + count <= limit;

    /// <summary>Counts bytes the response will carry, refusing them if they would cross the limit.</summary>
    public void Spend(long count)
    {
        if (!Fits(count))
        {
            throw Exceeded();
        }

        spent += count;
    }

    public ScryValidationException Exceeded() =>
        new($"The response is larger than this server allows ({limit} bytes). Ask for fewer rows or fewer members, or page it.");

    /// <summary>
    /// <paramref name="inner"/>, with every byte written through it spent from this budget — what the
    /// response itself is written through.
    /// </summary>
    public IBufferWriter<byte> Charging(IBufferWriter<byte> inner) =>
        new BudgetedBufferWriter(inner, this, charge: true);

    /// <summary>
    /// <paramref name="inner"/>, refusing once what is written through it would not fit, but spending
    /// nothing — for a batch entry, which is charged when it is copied into the envelope, and only if it
    /// is.
    /// </summary>
    public IBufferWriter<byte> Checking(IBufferWriter<byte> inner) =>
        new BudgetedBufferWriter(inner, this, charge: false);
}

/// <summary>
/// The writer a <see cref="ResponseBudget"/> hands out. Counted in <see cref="Advance"/>, the one point
/// at which bytes are actually written, rather than in <see cref="GetMemory"/>: a JSON writer asks for
/// room ahead of what it writes — three bytes for every character of a string it might have to
/// transcode — so refusing on the request would turn a value well inside the limit away.
/// </summary>
/// <remarks>
/// It refuses once. A JSON writer that fails mid-write still has the bytes it was going to commit, and
/// flushes them as it is disposed; a second refusal from there would replace the first with one that
/// says nothing new, and the inner writer would be advanced past what it handed out. So after the
/// first, what is committed is dropped.
/// </remarks>
sealed class BudgetedBufferWriter(IBufferWriter<byte> inner, ResponseBudget budget, bool charge) :
    IBufferWriter<byte>
{
    long written;
    bool refused;

    public void Advance(int count)
    {
        if (refused)
        {
            return;
        }

        // A checking writer measures what it holds itself; a charging one, what the response has
        // carried so far, which its own bytes are part of.
        var wanted = charge ? count : written + count;
        if (!budget.Fits(wanted))
        {
            refused = true;
            throw budget.Exceeded();
        }

        if (charge)
        {
            budget.Spend(count);
        }

        written += count;
        inner.Advance(count);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) =>
        inner.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) =>
        inner.GetSpan(sizeHint);
}
