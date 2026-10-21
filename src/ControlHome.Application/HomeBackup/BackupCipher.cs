using System.Security.Cryptography;
using System.Text;

namespace ControlHome.Application.HomeBackup;

/// <summary>
/// Encryption of the secrets in a backup: a key derived from the passphrase with PBKDF2-HMAC-SHA256
/// (<see cref="DefaultIterations"/> iterations, 16-byte random salt), and AES-256-GCM per value (12-byte random nonce,
/// 16-byte tag, the value's identity as associated data so a blob cannot be moved to another integration). A payload is
/// base64 of <c>nonce ‖ ciphertext ‖ tag</c>. The manifest carries a check value (a known text encrypted with the key), so
/// a wrong passphrase is recognized before anything is imported.
/// </summary>
public static class BackupCipher
{
    public const string Kdf = "PBKDF2-SHA256";
    public const string Cipher = "AES-256-GCM";
    public const int DefaultIterations = 600_000;

    /// <summary>Iteration counts accepted on import (a crafted backup must not make the server spin for minutes).</summary>
    public const int MinIterations = 100_000;
    public const int MaxIterations = 5_000_000;

    public const int MinPassphraseLength = 12;
    public const int MaxPassphraseLength = 256;

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string CheckText = "ControlHome backup";
    private const string CheckContext = "check";

    /// <summary>New encryption parameters (fresh salt) and the key for <paramref name="passphrase"/>.</summary>
    public static (BackupEncryption Encryption, byte[] Key) Create(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(passphrase, salt, DefaultIterations);
        var check = Encrypt(key, Encoding.UTF8.GetBytes(CheckText), CheckContext);
        return (new BackupEncryption(Kdf, DefaultIterations, Convert.ToBase64String(salt), Cipher, check), key);
    }

    /// <summary>The key for <paramref name="passphrase"/>, or <c>null</c> when the passphrase does not open this backup.</summary>
    /// <exception cref="FormatException">
    /// The parameters are not ones this version writes (unknown algorithm, missing or bad salt or check value, iterations
    /// out of range).
    /// </exception>
    public static byte[]? Open(BackupEncryption encryption, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(passphrase);
        if (encryption.Kdf != Kdf || encryption.Cipher != Cipher
            || encryption.Iterations is < MinIterations or > MaxIterations
            || string.IsNullOrEmpty(encryption.Salt) || string.IsNullOrEmpty(encryption.Check))
        {
            throw new FormatException("Unsupported backup encryption.");
        }

        var salt = Convert.FromBase64String(encryption.Salt);
        if (salt.Length < SaltBytes)
        {
            throw new FormatException("The backup's salt is too short.");
        }

        var key = DeriveKey(passphrase, salt, encryption.Iterations);
        try
        {
            return Encoding.UTF8.GetString(Decrypt(key, encryption.Check, CheckContext)) == CheckText ? key : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Encrypts <paramref name="plaintext"/>; <paramref name="context"/> must be given again to decrypt.</summary>
    public static string Encrypt(byte[] key, byte[] plaintext, string context)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plaintext);
        var payload = new byte[NonceBytes + plaintext.Length + TagBytes];
        var nonce = payload.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(
            nonce,
            plaintext,
            payload.AsSpan(NonceBytes, plaintext.Length),
            payload.AsSpan(NonceBytes + plaintext.Length, TagBytes),
            Encoding.UTF8.GetBytes(context));
        return Convert.ToBase64String(payload);
    }

    /// <exception cref="CryptographicException">Wrong key, wrong context, or the payload was changed.</exception>
    /// <exception cref="FormatException">The payload is not base64.</exception>
    public static byte[] Decrypt(byte[] key, string payload, string context)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = Convert.FromBase64String(payload);
        if (bytes.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("The encrypted value is too short.");
        }

        var plaintext = new byte[bytes.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(
            bytes.AsSpan(0, NonceBytes),
            bytes.AsSpan(NonceBytes, plaintext.Length),
            bytes.AsSpan(NonceBytes + plaintext.Length, TagBytes),
            plaintext,
            Encoding.UTF8.GetBytes(context));
        return plaintext;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
}
