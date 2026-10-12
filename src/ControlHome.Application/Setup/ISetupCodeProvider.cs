namespace ControlHome.Application.Setup;

/// <summary>
/// Holds the one-time first-run setup code (see <see cref="SetupCode"/>): generated at process start unless
/// <c>Setup:Code</c> is configured, rotated after too many failed attempts, logged and written to
/// <c>{DataPath}/setup-code.txt</c> only while setup is incomplete.
/// </summary>
public interface ISetupCodeProvider
{
    /// <summary>The code as shown to the operator (<c>XXXX-XXXX-XXXX</c> when generated).</summary>
    string Current { get; }

    /// <summary>Constant-time check of a candidate (dashes, spaces and case ignored); failures count towards rotation.</summary>
    bool Verify(string? candidate);

    /// <summary>Replaces the code with a new random one (and announces it).</summary>
    void Rotate();

    /// <summary>Setup is done: stop announcing the code and delete <c>setup-code.txt</c>.</summary>
    void OnSetupCompleted();
}
