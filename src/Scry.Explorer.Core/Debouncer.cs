namespace Scry;

/// <summary>
/// Trailing-edge debounce over <see cref="Task.Delay(int, Cancel)"/>: each call resets the timer, and
/// only the last action within the window runs. <see cref="Flush"/> runs it now instead, for the
/// moment a window cannot be waited for.
/// </summary>
public sealed class Debouncer(int delayMs = 500) :
    IDisposable
{
    CancelSource? pending;
    Func<Cancel, Task>? pendingAction;

    public void Run(Func<Task> action) =>
        Run(_ => action());

    /// <summary>
    /// As <see cref="Run(Func{Task})"/>, but hands the action the token for its own run. An action
    /// that outlasts the window can check it on the way back and drop a result a later call has
    /// already superseded.
    /// </summary>
    public void Run(Func<Cancel, Task> action)
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = new();
        pendingAction = action;
        _ = RunAfterDelay(action, pending.Token);
    }

    /// <summary>
    /// Runs the action still waiting for its window, now. The page closing is the case: what was
    /// going to be written when the window closed is written on the way out instead, and an action
    /// whose window has already closed is not run twice.
    /// </summary>
    public Task Flush()
    {
        var action = pendingAction;
        if (action is null)
        {
            return Task.CompletedTask;
        }

        pending?.Cancel();
        pending?.Dispose();
        pending = null;
        pendingAction = null;
        return Invoke(action, Cancel.None);
    }

    async Task RunAfterDelay(Func<Cancel, Task> action, Cancel token)
    {
        try
        {
            await Task.Delay(delayMs, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // Its window has closed, so a flush from here on has nothing of this action's to run.
        if (ReferenceEquals(pendingAction, action))
        {
            pendingAction = null;
        }

        await Invoke(action, token);
    }

    static async Task Invoke(Func<Cancel, Task> action, Cancel token)
    {
        try
        {
            await action(token);
        }
        catch (OperationCanceledException)
        {
            // The window closed under the action. Nothing to report.
        }
        catch (Exception exception)
        {
            // Nothing awaits this task, so an exception here has nowhere else to go: a GetValue after
            // the editor was torn down, or a failed interop call, would otherwise be an update that
            // silently never happened.
            await Console.Error.WriteLineAsync($"Scry: a debounced action failed. {exception}");
        }
    }

    public void Dispose()
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = null;
        pendingAction = null;
    }
}
