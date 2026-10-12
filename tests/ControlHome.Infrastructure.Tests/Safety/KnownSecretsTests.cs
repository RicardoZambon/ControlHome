using ControlHome.Application.Security;

namespace ControlHome.Infrastructure.Tests.Safety;

public sealed class KnownSecretsTests
{
    [Fact]
    public void Registered_values_are_masked_everywhere_in_a_text()
    {
        var secrets = new KnownSecrets();
        secrets.Register("s3cr3t-token");

        secrets.Redact("Bearer s3cr3t-token failed; retry with s3cr3t-token").ShouldBe("Bearer *** failed; retry with ***");
        secrets.ContainsSecret("x s3cr3t-token y").ShouldBeTrue();
        secrets.ContainsSecret("nothing here").ShouldBeFalse();
    }

    [Fact]
    public void The_longest_secret_is_masked_first()
    {
        var secrets = new KnownSecrets();
        secrets.Register("abcd");
        secrets.Register("abcdefgh");

        secrets.Redact("key=abcdefgh").ShouldBe("key=***");
    }

    [Fact]
    public void Short_and_null_values_are_not_tracked()
    {
        var secrets = new KnownSecrets();
        secrets.Register(null);
        secrets.Register("abc");

        secrets.IsEmpty.ShouldBeTrue();
        secrets.Redact("abc").ShouldBe("abc");
    }
}
