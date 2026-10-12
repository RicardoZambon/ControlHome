using ControlHome.Application.Setup;
using ControlHome.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Setup;

/// <summary>First-run setup options (configuration section <c>Setup</c>).</summary>
public sealed class SetupOptions
{
    public const string SectionName = "Setup";

    /// <summary>
    /// Fixed setup code instead of a random one (env <c>Setup__Code</c>; used by Umbrel's <c>$APP_PASSWORD</c> and E2E).
    /// Compared after normalization (case, dashes and spaces ignored). Never logged nor written to disk.
    /// </summary>
    public string? Code { get; set; }
}

/// <summary>
/// Singleton <see cref="ISetupCodeProvider"/>. The code is <c>Setup:Code</c> when configured, else 12 random Crockford base32
/// characters generated at process start. While setup is incomplete (<see cref="Announce"/>), a generated code is logged at
/// Warning and written to <c>{DataPath}/setup-code.txt</c> (owner-only on Unix); <see cref="OnSetupCompleted"/> deletes the
/// file. After <see cref="MaxFailedAttempts"/> failed verifications the code is replaced by a new random one.
/// </summary>
public sealed partial class SetupCodeProvider : ISetupCodeProvider
{
    public const int MaxFailedAttempts = 20;
    public const string FileName = "setup-code.txt";

    private readonly Lock _lock = new();
    private readonly string? _configuredCode;
    private readonly string? _configuredDisplay;
    private readonly DataDirectory _dataDirectory;
    private readonly ILogger<SetupCodeProvider> _logger;
    private string _code;
    private bool _isConfigured;
    private int _failedAttempts;
    private bool _announcing;

    public SetupCodeProvider(IOptions<SetupOptions> options, DataDirectory dataDirectory, ILogger<SetupCodeProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataDirectory = dataDirectory;
        _logger = logger;

        var configured = SetupCode.Normalize(options.Value.Code);
        if (configured.Length > 0)
        {
            _configuredCode = configured;
            _configuredDisplay = options.Value.Code!.Trim();
        }

        (_code, _isConfigured) = InitialCode();
    }

    /// <summary>The path of <c>setup-code.txt</c> (it exists only while a generated code is being announced).</summary>
    public string FilePath => Path.Combine(_dataDirectory.Root, FileName);

    public string Current
    {
        get
        {
            lock (_lock)
            {
                return _isConfigured ? _configuredDisplay! : SetupCode.Format(_code);
            }
        }
    }

    /// <summary>Whether the current code came from <c>Setup:Code</c> (false after a rotation).</summary>
    public bool IsConfigured
    {
        get
        {
            lock (_lock)
            {
                return _isConfigured;
            }
        }
    }

    public bool Verify(string? candidate)
    {
        lock (_lock)
        {
            if (SetupCode.Matches(_code, candidate))
            {
                return true;
            }

            _failedAttempts++;
            if (_failedAttempts >= MaxFailedAttempts)
            {
                LogRotatingAfterFailures(_failedAttempts);
                RotateLocked();
            }

            return false;
        }
    }

    public void Rotate()
    {
        lock (_lock)
        {
            RotateLocked();
        }
    }

    /// <summary>Setup is incomplete: logs the code (a configured one only by reference) and writes the file for a generated one.</summary>
    public void Announce()
    {
        lock (_lock)
        {
            _announcing = true;
            AnnounceLocked();
        }
    }

    public void OnSetupCompleted()
    {
        lock (_lock)
        {
            _announcing = false;
            DeleteFileLocked();
        }
    }

    /// <summary>
    /// Back to the state of a fresh, not-yet-set-up installation (Testing-only <c>POST /api/_test/reset</c>): the configured
    /// code (or a new random one), no failed attempts, announced again.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            (_code, _isConfigured) = InitialCode();
            _failedAttempts = 0;
            _announcing = true;
            AnnounceLocked();
        }
    }

    private (string Code, bool IsConfigured) InitialCode() =>
        _configuredCode is null ? (SetupCode.Generate(), false) : (_configuredCode, true);

    private void RotateLocked()
    {
        _code = SetupCode.Generate();
        _isConfigured = false;
        _failedAttempts = 0;

        // Rotation only happens while setup is incomplete, so the new code is always announced.
        _announcing = true;
        AnnounceLocked();
    }

    private void AnnounceLocked()
    {
        if (!_announcing)
        {
            return;
        }

        if (_isConfigured)
        {
            // A stale file from an earlier generated code would only confuse the operator.
            DeleteFileLocked();
            LogConfiguredCode();
            return;
        }

        var display = SetupCode.Format(_code);
        LogSetupCode(display);
        WriteFileLocked(display);
    }

    private void WriteFileLocked(string display)
    {
        var path = FilePath;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(_dataDirectory.Root);
                File.WriteAllText(path, display + Environment.NewLine);
                return;
            }

            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Directory.CreateDirectory(_dataDirectory.Root, OwnerOnly | UnixFileMode.UserExecute);
            using (var stream = new FileStream(
                path,
                new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = OwnerOnly }))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(display);
            }

            File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileWriteFailed(ex, path);
        }
    }

    private void DeleteFileLocked()
    {
        var path = FilePath;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileDeleteFailed(ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "First-run setup code: {SetupCode}")]
    private partial void LogSetupCode(string setupCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "First-run setup is waiting; use the configured setup code (Setup:Code)")]
    private partial void LogConfiguredCode();

    [LoggerMessage(Level = LogLevel.Warning, Message = "{FailedAttempts} wrong setup codes were entered; replacing the setup code")]
    private partial void LogRotatingAfterFailures(int failedAttempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the setup code file {Path}; the code is in the log")]
    private partial void LogFileWriteFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the setup code file {Path}")]
    private partial void LogFileDeleteFailed(Exception exception, string path);
}
