namespace ControlHome.Application.Common;

/// <summary>Application lock names (<see cref="Abstractions.IExclusiveTransaction"/>) shared by several features.</summary>
public static class AppLocks
{
    /// <summary>
    /// Everything that fills an empty home (backup import, the demo home): under this lock the "is the home empty?" check and
    /// the first writes happen together, so two of them can never both find the home empty.
    /// </summary>
    public const string HomeContent = "ControlHome.HomeContent";
}
