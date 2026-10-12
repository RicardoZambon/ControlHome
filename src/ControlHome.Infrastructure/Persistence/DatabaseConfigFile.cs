using System.Security.Cryptography;
using System.Text.Json;
using ControlHome.Infrastructure.Configuration;
using ControlHome.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// <c>{DataPath}/config/database.json</c>: the connection string saved by the pre-database setup wizard, encrypted with the
/// bootstrap key ring (<see cref="BootstrapDataProtection"/>). Written atomically (temp file + move), owner-only on Unix.
/// Never logs the connection string.
/// </summary>
public sealed partial class DatabaseConfigFile(
    DataDirectory dataDirectory,
    BootstrapDataProtection protection,
    ILogger<DatabaseConfigFile> logger)
{
    public const string FileName = "database.json";
    public const int CurrentVersion = 1;

    /// <summary>The Data Protection purpose of the stored value.</summary>
    public const string Purpose = "ControlHome.Database.ConnectionString.v1";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Where the file lives (it may not exist).</summary>
    public string FilePath => Path.Combine(dataDirectory.GetPath(DataDirectory.Config), FileName);

    /// <summary>The saved connection string; <c>null</c> when there is no file or it cannot be read or decrypted (logged).</summary>
    public string? Read()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), _json);
            if (document is not { Version: CurrentVersion, ProtectedConnectionString: { Length: > 0 } protectedValue })
            {
                LogUnreadable(path, "unexpected content");
                return null;
            }

            return protection.CreateProtector(Purpose).Unprotect(protectedValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException)
        {
            // Only the exception type: the messages are not needed and are better kept out of the log here.
            LogUnreadable(path, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Encrypts and writes the connection string (replacing any existing file).</summary>
    public void Write(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var content = JsonSerializer.SerializeToUtf8Bytes(
            new Document(CurrentVersion, protection.CreateProtector(Purpose).Protect(connectionString)),
            _json);

        var directory = dataDirectory.EnsureSubdirectory(DataDirectory.Config);
        var path = Path.Combine(directory, FileName);
        var temp = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            // A crash leaves either the old file or the new one, never a half-written one.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        LogSaved(path);
    }

    private sealed record Document(int Version, string? ProtectedConnectionString);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved the database connection to {Path} (encrypted)")]
    private partial void LogSaved(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ignoring {Path}: it cannot be read or decrypted ({Reason}); the setup wizard will ask for the database again")]
    private partial void LogUnreadable(string path, string reason);
}
