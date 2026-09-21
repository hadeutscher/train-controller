namespace DuploTrain.Core;

public static class InputThread
{
    /// <summary>Runs a blocking input loop on a dedicated background thread.
    ///
    /// Explicit rather than <c>TaskCreationOptions.LongRunning</c> so that
    /// <c>IsBackground</c> is visibly true at the call site — an input loop must
    /// never be the reason the process refuses to exit — and so the thread has a
    /// name that shows up in a debugger or a hang dump.
    ///
    /// The returned task still carries any exception, so a source that dies is
    /// reported rather than silently stopping.</summary>
    public static Task Run(string name, Action body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                body();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
        };

        thread.Start();
        return completion.Task;
    }
}
