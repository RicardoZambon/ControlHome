using ControlHome.Connectors.DahuaHttp.Protocol;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>
/// The hand-written Digest answer of the one-shot door release, checked against the published test vectors of RFC 2617
/// (§3.5, MD5 with qop=auth) and RFC 7616 (§3.9.1, SHA-256 and MD5).
/// </summary>
public sealed class DigestChallengeTests
{
    [Fact]
    public void Md5_with_qop_auth_matches_rfc_2617()
    {
        var challenge = DigestChallenge.Parse(
            "Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");

        var header = challenge.ShouldNotBeNull().Authorize("GET", "/dir/index.html", "Mufasa", "Circle Of Life", cnonce: "0a4f113b");

        header.ShouldContain("response=\"6629fae49393a05397450978507c4ef1\"");
        header.ShouldContain("qop=auth, nc=00000001, cnonce=\"0a4f113b\"");
        header.ShouldContain("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"");
        header.ShouldContain("uri=\"/dir/index.html\"");
        DahuaFixtures.IsValidDigest(header, "GET", "Mufasa", "Circle Of Life").ShouldBeTrue();
    }

    [Theory]
    [InlineData("SHA-256", "753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1")]
    [InlineData("MD5", "8ca523f5e9506fed4657c9700eebdbec")]
    public void Sha256_and_md5_match_rfc_7616(string algorithm, string expected)
    {
        var challenge = DigestChallenge.Parse(
            $"Digest realm=\"http-auth@example.org\", qop=\"auth, auth-int\", algorithm={algorithm}, nonce=\"7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v\", opaque=\"FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS\"");

        var header = challenge.ShouldNotBeNull().Authorize("GET", "/dir/index.html", "Mufasa", "Circle of Life", cnonce: "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ");

        header.ShouldContain($"response=\"{expected}\"");
        header.ShouldContain($"algorithm={algorithm}");
    }

    [Fact]
    public void Only_a_supported_digest_challenge_is_used()
    {
        DigestChallenge.Select(["Basic realm=\"SS\"", "Digest realm=\"Login to SYNTH\", nonce=\"abc\", qop=\"auth\""]).ShouldNotBeNull().Realm.ShouldBe("Login to SYNTH");
        DigestChallenge.Select(["Basic realm=\"SS\""]).ShouldBeNull("never Basic");
        DigestChallenge.Parse("Digest realm=\"x\", qop=\"auth\"").ShouldBeNull("no nonce");
        DigestChallenge.Parse("Digest realm=\"x\", nonce=\"n\", qop=\"auth-int\"").ShouldBeNull("auth-int only");
        DigestChallenge.Parse("Digest realm=\"x\", nonce=\"n\", algorithm=SHA-512-256").ShouldBeNull("unsupported algorithm");
        DigestChallenge.Parse("Digest realm=\"a, b\", nonce=\"n\"").ShouldNotBeNull().Realm.ShouldBe("a, b");
    }

    [Fact]
    public void Without_qop_the_rfc_2069_form_is_used()
    {
        var header = DigestChallenge.Parse("Digest realm=\"r\", nonce=\"n\"").ShouldNotBeNull().Authorize("GET", "/cgi-bin/x.cgi?a=1", "u", "p");

        header.ShouldNotContain("qop=");
        header.ShouldNotContain("cnonce=");
        DahuaFixtures.IsValidDigest(header, "GET", "u", "p").ShouldBeTrue();
    }
}
