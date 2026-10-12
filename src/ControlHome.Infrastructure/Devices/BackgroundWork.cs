namespace ControlHome.Infrastructure.Devices;

/// <summary>Starts long-lived background work without capturing the caller's execution context (e.g. an HTTP request's).</summary>
internal static class BackgroundWork
{
    public static T WithoutFlow<T>(Func<T> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (ExecutionContext.IsFlowSuppressed())
        {
            return create();
        }

        using (ExecutionContext.SuppressFlow())
        {
            return create();
        }
    }

    /// <summary><see cref="Task.Run(Func{Task})"/> without the caller's execution context.</summary>
    public static Task Run(Func<Task> work) => WithoutFlow(() => Task.Run(work));
}
