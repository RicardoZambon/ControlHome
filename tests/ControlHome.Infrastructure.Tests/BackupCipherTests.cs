using System.Security.Cryptography;
using System.Text;
using ControlHome.Application.HomeBackup;

namespace ControlHome.Infrastructure.Tests;

/// <summary>Backup secrets: PBKDF2-SHA256 (600 000 iterations) + AES-256-GCM, bound to their context.</summary>
public sealed class BackupCipherTests
{
    private const string Passphrase = "correct horse battery staple";

    [Fact]
    public void A_value_encrypted_with_the_passphrase_decrypts_with_it_only()
    {
        var (encryption, key) = BackupCipher.Create(Passphrase);

        encryption.Kdf.ShouldBe("PBKDF2-SHA256");
        encryption.Cipher.ShouldBe("AES-256-GCM");
        encryption.Iterations.ShouldBe(600_000);
        Convert.FromBase64String(encryption.Salt).Length.ShouldBe(16);
        var payload = BackupCipher.Encrypt(key, Encoding.UTF8.GetBytes("s3cret-token"), "integration:1");
        payload.ShouldNotContain("s3cret");

        var reopened = BackupCipher.Open(encryption, Passphrase).ShouldNotBeNull();
        Encoding.UTF8.GetString(BackupCipher.Decrypt(reopened, payload, "integration:1")).ShouldBe("s3cret-token");
        BackupCipher.Open(encryption, "wrong passphrase!!").ShouldBeNull();
    }

    [Fact]
    public void Tampered_moved_or_truncated_payloads_are_rejected()
    {
        var (_, key) = BackupCipher.Create(Passphrase);
        var payload = BackupCipher.Encrypt(key, Encoding.UTF8.GetBytes("value"), "integration:1");
        var bytes = Convert.FromBase64String(payload);
        bytes[^1] ^= 0x01;

        Should.Throw<CryptographicException>(() => BackupCipher.Decrypt(key, Convert.ToBase64String(bytes), "integration:1"));
        Should.Throw<CryptographicException>(() => BackupCipher.Decrypt(key, payload, "integration:2"), "bound to its integration");
        Should.Throw<CryptographicException>(() => BackupCipher.Decrypt(key, Convert.ToBase64String(new byte[10]), "integration:1"));
        BackupCipher.Encrypt(key, Encoding.UTF8.GetBytes("value"), "integration:1").ShouldNotBe(payload, "a fresh nonce every time");
    }

    [Theory]
    [InlineData("PBKDF2-SHA256", 1_000, "AES-256-GCM")]
    [InlineData("PBKDF2-SHA256", 50_000_000, "AES-256-GCM")]
    [InlineData("scrypt", 600_000, "AES-256-GCM")]
    [InlineData("PBKDF2-SHA256", 600_000, "AES-128-CBC")]
    public void Parameters_this_version_does_not_write_are_refused(string kdf, int iterations, string cipher)
    {
        var (encryption, _) = BackupCipher.Create(Passphrase);

        Should.Throw<FormatException>(() => BackupCipher.Open(encryption with { Kdf = kdf, Iterations = iterations, Cipher = cipher }, Passphrase));
    }
}
