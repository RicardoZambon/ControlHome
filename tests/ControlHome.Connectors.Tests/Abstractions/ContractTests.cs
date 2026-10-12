using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.Abstractions;

public sealed class ContractTests
{
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    private static readonly LocalizedText _title = new("Sign in", "Entrar");

    [Fact]
    public void Flow_steps_serialize_with_a_type_discriminator()
    {
        FlowStep[] steps =
        [
            new FormStep("account", _title, null, [new FieldSchema("token", _title, new StringSchema(512), true, true, null)]),
            new ExternalUrlStep("authorize", _title, null, new Uri("https://example.com/authorize"), null),
            new ProgressStep("press", _title, null, TimeSpan.FromSeconds(60)),
            new ChooseStep("hub", _title, null, [new FlowChoice("a", _title, null)]),
            new DoneStep("Home", Json("""{"host":"10.0.0.2"}""")),
            new AbortStep("timeout", _title),
        ];

        var types = steps.Select(s => JsonSerializer.SerializeToElement(s, _web).GetProperty("type").GetString()).ToList();

        types.ShouldBe(["form", "externalUrl", "progress", "choose", "done", "abort"]);
        var form = JsonSerializer.Deserialize<FlowStep>(JsonSerializer.Serialize<FlowStep>(steps[0], _web), _web);
        form.ShouldBeOfType<FormStep>().Fields.Single().Schema.ShouldBe(new StringSchema(512));
    }

    [Fact]
    public void A_done_step_never_serializes_its_secrets()
    {
        var done = new DoneStep("Home", Json("""{"host":"10.0.0.2"}"""))
        {
            Secrets = new Dictionary<string, string> { ["password"] = "CANARY-do-not-leak" },
        };

        var json = JsonSerializer.Serialize<FlowStep>(done, _web);

        json.ShouldNotContain("CANARY");
        json.ShouldNotContain("secrets", Case.Insensitive);
        done.ToString().ShouldNotContain("CANARY");
    }

    [Fact]
    public void Field_values_are_validated_like_command_arguments()
    {
        FieldSchema[] fields =
        [
            new("host", _title, new StringSchema(255), Required: true, Secret: false, null),
            new("port", _title, new NumberSchema(1, 65535, 1, null, Integer: true), Required: false, Secret: false, null),
        ];

        FieldSchemaValidator.Validate(fields, Json("""{"host":"broker","port":1883}""")).IsValid.ShouldBeTrue();
        FieldSchemaValidator.Validate(fields, Json("""{"port":70000,"tls":true}""")).Errors.ShouldBe(
        [
            new ArgumentError("tls", ValueErrorKind.Unknown),
            new ArgumentError("host", ValueErrorKind.Required),
            new ArgumentError("port", ValueErrorKind.OutOfRange),
        ]);
    }

    [Fact]
    public void Command_outcomes_are_distinguishable()
    {
        CommandOutcome.AcceptedOutcome.ShouldBeOfType<CommandOutcome.Accepted>();
        CommandOutcome.CompletedOutcome.ShouldBeOfType<CommandOutcome.Completed>();
        CommandOutcome.Reject(CommandRejectionCodes.Duplicate, "again").ShouldBe(new CommandOutcome.Rejected("duplicate", "again"));
    }

    [Fact]
    public void Capability_bindings_carry_constraints_as_json()
    {
        var binding = CapabilityBinding.Of(CapabilityIds.Level, new CapabilityConstraints(
            new Dictionary<string, AttributeConstraint> { ["level"] = new(Min: 1) }));

        CapabilityConstraints.Parse(binding.Constraints).For("level")!.Min.ShouldBe(1);
        CapabilityBinding.Of(CapabilityIds.Switch).Constraints.ShouldBeNull();
    }

    [Fact]
    public void Localized_text_picks_the_locale()
    {
        _title.For("pt-BR").ShouldBe("Entrar");
        _title.For("en").ShouldBe("Sign in");
        _title.For("de").ShouldBe("Sign in");
    }

    [Fact]
    public async Task Quota_counters_reset_every_period()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 23, 59, 0, TimeSpan.Zero));
        var counter = new InMemoryQuotaCounter(time);
        var limit = new QuotaLimit("tuya:test:calls", QuotaPeriod.Day, 2);
        var ct = TestContext.Current.CancellationToken;

        (await counter.TryConsumeAsync(limit, cancellationToken: ct)).ShouldBeTrue();
        (await counter.TryConsumeAsync(limit, cancellationToken: ct)).ShouldBeTrue();
        (await counter.TryConsumeAsync(limit, cancellationToken: ct)).ShouldBeFalse();
        (await counter.GetAsync(limit.Counter, QuotaPeriod.Day, ct)).ShouldBe(2);

        time.Advance(TimeSpan.FromMinutes(1)); // midnight UTC: a new day
        (await counter.GetAsync(limit.Counter, QuotaPeriod.Day, ct)).ShouldBe(0);
        (await counter.TryConsumeAsync(limit, cancellationToken: ct)).ShouldBeTrue();
    }

    [Fact]
    public void Quota_periods_start_on_utc_boundaries()
    {
        var now = new DateTimeOffset(2026, 10, 12, 21, 34, 56, TimeSpan.FromHours(-3)); // 2026-10-13 00:34:56Z

        QuotaCounterExtensions.PeriodStart(QuotaPeriod.Minute, now).ShouldBe(new DateTimeOffset(2026, 10, 13, 0, 34, 0, TimeSpan.Zero));
        QuotaCounterExtensions.PeriodStart(QuotaPeriod.Hour, now).ShouldBe(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
        QuotaCounterExtensions.PeriodStart(QuotaPeriod.Day, now).ShouldBe(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
        QuotaCounterExtensions.PeriodStart(QuotaPeriod.Month, now).ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task The_empty_secret_store_has_no_secrets()
    {
        (await EmptySecretStore.Instance.GetAsync("token", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await EmptySecretStore.Instance.GetDeviceSecretAsync("d", "key", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
