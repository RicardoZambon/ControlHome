using ControlHome.Application.Setup;
using ControlHome.Infrastructure.Configuration;
using ControlHome.Infrastructure.Setup;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Tests;

public sealed class SetupCodeTests
{
    [Fact]
    public void Generated_codes_are_12_crockford_base32_characters_shown_in_groups_of_four()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => SetupCode.Generate()).ToList();

        codes.ShouldAllBe(c => c.Length == 12 && c.All(ch => SetupCode.Alphabet.Contains(ch)));
        codes.Distinct().Count().ShouldBe(codes.Count);
        SetupCode.Format("ABCD2345WXYZ").ShouldBe("ABCD-2345-WXYZ");
        SetupCode.Alphabet.ShouldNotContain('I');
        SetupCode.Alphabet.ShouldNotContain('L');
        SetupCode.Alphabet.ShouldNotContain('O');
        SetupCode.Alphabet.ShouldNotContain('U');
    }

    [Theory]
    [InlineData("ABCD-2345-WXYZ", true)]
    [InlineData("abcd-2345-wxyz", true)]
    [InlineData(" ABCD 2345 WXYZ ", true)]
    [InlineData("ABCD2345WXYZ", true)]
    [InlineData("ABCD-2345-WXY", false)]
    [InlineData("ABCD-2345-WXYZ0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Candidates_match_after_normalization(string? candidate, bool expected)
    {
        SetupCode.Matches("ABCD2345WXYZ", candidate).ShouldBe(expected);
    }

    [Fact]
    public void An_empty_code_never_matches()
    {
        SetupCode.Matches(string.Empty, string.Empty).ShouldBeFalse();
        SetupCode.Matches(string.Empty, "-").ShouldBeFalse();
    }
}

public sealed class SetupCodeProviderTests : IDisposable
{
    private const string CodePattern = "^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$";

    private readonly string _dataPath = Directory.CreateTempSubdirectory("controlhome-setupcode-").FullName;
    private readonly ListLogger _logger = new();

    public void Dispose() => Directory.Delete(_dataPath, recursive: true);

    [Fact]
    public void Without_configuration_a_random_code_is_generated_per_process()
    {
        var first = Create(configured: null);
        var second = Create(configured: null);

        first.Current.ShouldMatch(CodePattern);
        first.IsConfigured.ShouldBeFalse();
        second.Current.ShouldNotBe(first.Current);
        first.Verify(first.Current).ShouldBeTrue();
        first.Verify(first.Current.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)).ShouldBeTrue();
        first.Verify(second.Current).ShouldBeFalse();
    }

    [Fact]
    public void Configured_code_is_used_as_is_and_compared_normalized()
    {
        var provider = Create(configured: "E2E-SETUP-CODE-1");

        provider.IsConfigured.ShouldBeTrue();
        provider.Current.ShouldBe("E2E-SETUP-CODE-1");
        provider.Verify("e2e setup code 1").ShouldBeTrue();
        provider.Verify("E2E-SETUP-CODE-2").ShouldBeFalse();
    }

    [Fact]
    public void Nothing_is_written_or_logged_before_the_code_is_announced()
    {
        var provider = Create(configured: null);

        _ = provider.Current;

        File.Exists(provider.FilePath).ShouldBeFalse();
        _logger.Messages.ShouldBeEmpty();
    }

    [Fact]
    public void Announcing_a_generated_code_logs_it_at_warning_and_writes_the_file_until_setup_completes()
    {
        var provider = Create(configured: null);

        provider.Announce();

        File.ReadAllText(provider.FilePath).Trim().ShouldBe(provider.Current);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(provider.FilePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _logger.Messages.ShouldContain((LogLevel.Warning, $"First-run setup code: {provider.Current}"));

        provider.OnSetupCompleted();

        File.Exists(provider.FilePath).ShouldBeFalse();
    }

    [Fact]
    public void A_configured_code_is_never_logged_nor_written_and_a_stale_file_is_removed()
    {
        File.WriteAllText(Path.Combine(_dataPath, SetupCodeProvider.FileName), "OLD0-OLD0-OLD0");
        var provider = Create(configured: "operator-secret-123");

        provider.Announce();

        File.Exists(provider.FilePath).ShouldBeFalse();
        _logger.Messages.ShouldNotBeEmpty();
        _logger.Messages.ShouldAllBe(m => !m.Message.Contains("operator-secret-123", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Twenty_failed_attempts_rotate_to_a_new_random_code()
    {
        var provider = Create(configured: "E2E-SETUP-CODE-1");
        provider.Announce();

        for (var i = 1; i < SetupCodeProvider.MaxFailedAttempts; i++)
        {
            provider.Verify($"wrong-{i}").ShouldBeFalse();
        }

        provider.Verify("E2E-SETUP-CODE-1").ShouldBeTrue(); // 19 failures: still the same code
        provider.Verify("wrong-20").ShouldBeFalse();

        provider.IsConfigured.ShouldBeFalse();
        provider.Current.ShouldMatch(CodePattern);
        provider.Verify("E2E-SETUP-CODE-1").ShouldBeFalse();
        provider.Verify(provider.Current).ShouldBeTrue();
        File.ReadAllText(provider.FilePath).Trim().ShouldBe(provider.Current);
        _logger.Messages.ShouldContain((LogLevel.Warning, $"First-run setup code: {provider.Current}"));
    }

    [Fact]
    public void Reset_restores_the_configured_code_and_the_attempt_counter()
    {
        var provider = Create(configured: "E2E-SETUP-CODE-1");
        provider.Rotate();
        provider.Verify("E2E-SETUP-CODE-1").ShouldBeFalse();

        provider.Reset();

        provider.IsConfigured.ShouldBeTrue();
        provider.Verify("E2E-SETUP-CODE-1").ShouldBeTrue();
        File.Exists(provider.FilePath).ShouldBeFalse();
        for (var i = 1; i < SetupCodeProvider.MaxFailedAttempts; i++)
        {
            provider.Verify("wrong").ShouldBeFalse();
        }

        provider.IsConfigured.ShouldBeTrue(); // the counter started again from zero
    }

    [Fact]
    public void Reset_without_configuration_generates_and_announces_a_fresh_code()
    {
        var provider = Create(configured: null);
        var before = provider.Current;

        provider.Reset();

        provider.Current.ShouldNotBe(before);
        File.ReadAllText(provider.FilePath).Trim().ShouldBe(provider.Current);
    }

    private SetupCodeProvider Create(string? configured)
    {
        var dataDirectory = new DataDirectory(Options.Create(new ControlHomeOptions { DataPath = _dataPath }), new TestEnvironment());
        return new SetupCodeProvider(Options.Create(new SetupOptions { Code = configured }), dataDirectory, _logger);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "ControlHome.Infrastructure.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ListLogger : ILogger<SetupCodeProvider>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add((logLevel, formatter(state, exception)));
        }
    }
}
