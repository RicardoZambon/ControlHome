namespace ControlHome.Infrastructure.Persistence;

/// <summary>An application lock (<c>sp_getapplock</c>) could not be acquired in time; not retried.</summary>
public sealed class ApplicationLockException : InvalidOperationException
{
    public ApplicationLockException()
    {
    }

    public ApplicationLockException(string message)
        : base(message)
    {
    }

    public ApplicationLockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
