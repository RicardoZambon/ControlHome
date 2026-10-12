using System.Diagnostics;
using System.Globalization;
using System.Net;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// Review focus 4: a connector flooding the pipeline (1 000 reports/s for 5 s over 200 attributes) must not stall commands:
/// the time from sending a command to its state change reaching clients stays below 500 ms at the 95th percentile.
/// </summary>
[Collection(DevicesCollection.Name)]
public sealed class BurstTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    private const int Rate = 1_000;
    private const double Seconds = 5;
    private const int Total = (int)(Rate * Seconds);
    private static readonly TimeSpan _commandInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Commands_stay_fast_while_a_connector_floods_the_pipeline()
    {
        var (admin, member, _) = await SignInAllAsync();
        var processor = Factory.Services.GetRequiredService<StateProcessor>();
        var receivedBefore = processor.Received;
        await CreateIntegrationAsync(
            admin,
            BurstConnectorFactory.Key,
            string.Create(CultureInfo.InvariantCulture, $$"""{"attributes":200,"rate":{{Rate}},"seconds":{{Seconds}},"delaySeconds":2}"""));
        var device = (await WaitForDevicesAsync(member, 1)).Single();
        var endpoint = MainEndpoint(device).Id;
        var on = AttributeId(device, CapabilityIds.Switch, "on");
        await RecordingRealtimePublisher.WaitUntilAsync(() => Factory.Spy.BurstReports > 0, "the burst started");
        var burst = Stopwatch.StartNew();

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
        var reports = processor.Received - receivedBefore;
        var p95 = Percentile(endToEnd, 0.95);
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"BURST: {Factory.Spy.BurstReports} reports sent, {reports} queued ({processor.Coalesced} coalesced, {processor.Dropped} dropped); " +
            $"{endToEnd.Count} commands during the burst: command → client delta p50 {Percentile(endToEnd, 0.5):0.0} ms, " +
            $"p95 {p95:0.0} ms, max {endToEnd.Max():0.0} ms; POST → 202 p95 {Percentile(accepted, 0.95):0.0} ms");
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Console.WriteLine(report);

        reports.ShouldBeGreaterThanOrEqualTo(Factory.Spy.BurstReports, report);
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
