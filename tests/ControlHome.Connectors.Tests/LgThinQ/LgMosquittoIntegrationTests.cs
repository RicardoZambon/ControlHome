using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using MQTTnet;
using MQTTnet.Protocol;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>
/// The real MQTT path against Mosquitto with TLS client-certificate authentication (the test CA plays LG's: it signs the
/// runtime's CSR through the fake REST API and the broker trusts it): connect with the issued certificate, subscribe the
/// topic LG named, merge partial reports, and reconnect + resubscribe after a broker restart.
/// </summary>
[Collection(LgMosquittoCollection.Name)]
public sealed class LgMosquittoIntegrationTests(LgMosquittoFixture broker) : IAsyncDisposable
{
    private const string Ac = "test-ac-0001";

    private readonly FakeLgCloud _cloud = new(broker.Ca);

    public ValueTask DisposeAsync()
    {
        _cloud.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_runtime_connects_with_its_issued_certificate_and_merges_reports_across_a_broker_restart()
    {
        _cloud.Account(broker.Host, broker.Port, (Ac, "ac"));
        var options = LgHarness.Options(_cloud) with
        {
            ReconnectInitial = TimeSpan.FromMilliseconds(250),
            ReconnectMax = TimeSpan.FromSeconds(2),
            ConnectionCheckInterval = TimeSpan.FromSeconds(1),
            MqttConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var factory = new LgThinQConnectorFactory(null, options, null);
        var secrets = new MemorySecretStore((LgSecrets.Pat, LgHarness.Pat), (LgSecrets.ClientId, LgHarness.ClientId));
        var context = new RecordingContext(TimeProvider.System);
        await using var runtime = (LgThinQRuntime)factory.CreateRuntime(
            new IntegrationInstance(LgHarness.IntegrationId, "LG ThinQ", LgFixtures.Parse("""{"country":"BR","energy":false}"""), secrets));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = Task.Run(() => runtime.RunAsync(context, stop.Token), TestContext.Current.CancellationToken);
        try
        {
            await Waiting.UntilAsync(() => runtime.MqttConnected, "the runtime is connected over TLS with its client certificate", running);
            _cloud.Certificates.ShouldBe(1);
            await Waiting.UntilAsync(() => Latest(context, "targetTemperature") == StateValue.Number(19), "the first poll is reported", running);

            await PublishAsync("""{"deviceId":"test-ac-0001","deviceType":"DEVICE_AIR_CONDITIONER","pushType":"DEVICE_STATUS","report":{"temperatureInUnits":[{"unit":"C","targetTemperature":23}]}}""");
            await Waiting.UntilAsync(() => Latest(context, "targetTemperature") == StateValue.Number(23), "the partial report is merged", running);
            Latest(context, "mode").ShouldBe(StateValue.String("cool"));

            await broker.RestartAsync();
            await Waiting.UntilAsync(() => runtime.Connections >= 2 && runtime.MqttConnected, "the runtime reconnected after the restart", running);
            context.Statuses.ShouldContain(s => s.Status == IntegrationStatus.Degraded);

            // The broker forgot every session: the runtime subscribed again, so the next report still arrives.
            await PublishAsync("""{"deviceId":"test-ac-0001","deviceType":"DEVICE_AIR_CONDITIONER","pushType":"DEVICE_STATUS","report":{"airFlow":{"windStrength":"HIGH"}}}""");
            await Waiting.UntilAsync(() => Latest(context, "fanMode") == StateValue.String("high"), "a report after the restart is merged", running);
            Latest(context, "targetTemperature").ShouldBe(StateValue.Number(23));
            _cloud.Certificates.ShouldBe(1); // the stored certificate is reused
        }
        finally
        {
            await stop.CancelAsync();
            await StoppedAsync(running);
        }
    }

    [Fact]
    public async Task A_certificate_the_broker_does_not_trust_is_refused()
    {
        using var stranger = new TestCertificateAuthority();
        using var cloud = new FakeLgCloud(stranger);
        cloud.Account(broker.Host, broker.Port, (Ac, "ac"));
        using var factory = new LgThinQConnectorFactory(null, LgHarness.Options(cloud) with { TrustedRootsOverride = [broker.Ca.Certificate] }, null);
        var secrets = new MemorySecretStore((LgSecrets.Pat, LgHarness.Pat), (LgSecrets.ClientId, LgHarness.ClientId));
        var context = new RecordingContext(TimeProvider.System);
        await using var runtime = (LgThinQRuntime)factory.CreateRuntime(
            new IntegrationInstance(Guid.NewGuid(), "LG ThinQ", LgFixtures.Parse("""{"country":"BR","energy":false}"""), secrets));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = Task.Run(() => runtime.RunAsync(context, stop.Token), TestContext.Current.CancellationToken);
        try
        {
            await Waiting.UntilAsync(
                () => context.Statuses.Any(s => s.Status == IntegrationStatus.Degraded && s.Message!.Contains("Cannot connect", StringComparison.Ordinal)),
                "the connection is refused",
                running);
            runtime.MqttConnected.ShouldBeFalse();
        }
        finally
        {
            await stop.CancelAsync();
            await StoppedAsync(running);
        }
    }

    private static async Task StoppedAsync(Task running)
    {
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private static StateValue? Latest(RecordingContext context, string attribute) =>
        context.States.LastOrDefault(s => s.ExternalDeviceId == Ac && s.CapabilityId == CapabilityIds.Thermostat && s.AttributeKey == attribute)?.Value;

    private async Task PublishAsync(string payload)
    {
        using var publisher = await broker.PublisherAsync();
        await publisher.PublishAsync(
            new MqttApplicationMessageBuilder().WithTopic(FakeLgCloud.Topic).WithPayload(payload).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            TestContext.Current.CancellationToken);
        await publisher.DisconnectAsync();
    }
}
