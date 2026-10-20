using System.Diagnostics;
using System.Globalization;
using System.Net;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// Review focus 4: a connector flooding the pipeline (1 000 reports/s for 5 s over 200 attributes) must not stall commands
/// or the UI: the time from sending a command to its state change reaching clients stays below 500 ms at the 95th
/// percentile, no report is dropped, and clients get one batched StateChanged per publish window, not one per report.
/// </summary>
[Collection(DevicesCollection.Name)]
public sealed class BurstTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    private const int Rate = 1_000;
    private const int Attributes = 200;
    private const double Seconds = 5;
    private const int Total = (int)(Rate * Seconds);
    private static readonly TimeSpan _commandInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Commands_stay_fast_while_a_connector_floods_the_pipeline()
    {
        var (admin, member, _) = await SignInAllAsync();
        var processor = Factory.Services.GetRequiredService<StateProcessor>();
        var receivedBefore = processor.Received;
        var droppedBefore = processor.Dropped;
        await CreateIntegrationAsync(
            admin,
            BurstConnectorFactory.Key,
            string.Create(CultureInfo.InvariantCulture, $$"""{"attributes":{{Attributes}},"rate":{{Rate}},"seconds":{{Seconds}},"delaySeconds":2}"""));
        var device = (await WaitForDevicesAsync(member, 1)).Single();
        var endpoint = MainEndpoint(device).Id;
        var on = AttributeId(device, CapabilityIds.Switch, "on");
        await RecordingRealtimePublisher.WaitUntilAsync(() => Factory.Spy.BurstReports > 0, "the burst started");
        var burst = Stopwatch.StartNew();
        var burstStart = Stopwatch.GetTimestamp();

        // Commands every 100 ms for as long as the burst runs.
        var endToEnd = new List<double>();
        var accepted = new List<double>();
        for (var i = 0; Factory.Spy.BurstReports < Total * 0.98 && burst.Elapsed < TimeSpan.FromSeconds(Seconds + 2); i++)
        {
            var turnOn = i % 2 == 0;
            var started = Stopwatch.GetTimestamp();
            using (var response = await SendCommandAsync(member, endpoint, CapabilityIds.Switch, turnOn ? "turnOn" : "turnOff"))
            {
                await response.ShouldHaveStatusAsync(HttpStatusCode.Accepted);
                accepted.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            bool Reached((long At, StateDelta Delta) d) =>
                d.At >= started && d.Delta.AttributeId == on && d.Delta.Value == StateValue.Bool(turnOn);
            await RecordingRealtimePublisher.WaitUntilAsync(() => Factory.Realtime.Deltas.Any(Reached), $"command {i} reached the clients", timeoutSeconds: 10);
            endToEnd.Add(Stopwatch.GetElapsedTime(started, Factory.Realtime.Deltas.First(Reached).At).TotalMilliseconds);

            var next = Stopwatch.GetElapsedTime(started);
            if (next < _commandInterval)
            {
                await Task.Delay(_commandInterval - next, Ct);
            }
        }

        await RecordingRealtimePublisher.WaitUntilAsync(() => Factory.Spy.BurstReports >= Total * 0.98, "the burst finished", timeoutSeconds: 15);
        var burstEnd = Stopwatch.GetTimestamp();
        await processor.WhenIdleAsync(TimeSpan.FromSeconds(10));
        var reportsAccepted = processor.Received - receivedBefore;
        var dropped = processor.Dropped - droppedBefore;
        var p95 = Percentile(endToEnd, 0.95);

        // The UI side: the deltas reach clients batched (one StateChanged per publish window), never one message per report.
        var stateMessages = Factory.Realtime.Messages
            .Where(m => m.Name == HomeHubMessages.StateChanged && m.At >= burstStart && m.At <= burstEnd)
            .Select(m => (IReadOnlyList<StateDelta>)m.Payload!)
            .ToList();
        var burstSeconds = Stopwatch.GetElapsedTime(burstStart, burstEnd).TotalSeconds;
        var window = Factory.Services.GetRequiredService<IOptions<DeviceRuntimeOptions>>().Value.PublishWindow;
        var maxMessages = (int)Math.Ceiling(burstSeconds / window.TotalSeconds * 1.25) + 10;
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"BURST: {Factory.Spy.BurstReports} reports sent, {reportsAccepted} accepted ({processor.Coalesced} coalesced in total, {dropped} dropped); " +
            $"{stateMessages.Count} StateChanged messages in {burstSeconds:0.0} s (largest {stateMessages.Select(m => m.Count).DefaultIfEmpty().Max()} deltas); " +
            $"{endToEnd.Count} commands during the burst: command → client delta p50 {Percentile(endToEnd, 0.5):0.0} ms, " +
            $"p95 {p95:0.0} ms, max {endToEnd.Max():0.0} ms; POST → 202 p95 {Percentile(accepted, 0.95):0.0} ms");
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Console.WriteLine(report);

        // No burst key was lost (the queue did not overflow, no value or attribute was rejected).
        dropped.ShouldBe(0, report);
        stateMessages.Count.ShouldBeLessThanOrEqualTo(maxMessages, report);
        stateMessages.ShouldAllBe(m => m.Count <= Attributes + 1 && m.Select(d => d.AttributeId).Distinct().Count() == m.Count, report);
        endToEnd.Count.ShouldBeGreaterThanOrEqualTo(20, report);
        p95.ShouldBeLessThan(500, report);
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.Order().ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
