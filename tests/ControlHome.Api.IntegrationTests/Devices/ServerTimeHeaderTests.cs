using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.Http;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// API responses say what time it is on the runtime clock (<c>X-ControlHome-Time</c>), so the UI's "3 minutes ago" and
/// chart ends follow the server, not a phone's clock.
/// </summary>
[Collection(DevicesCollection.Name)]
public sealed class ServerTimeHeaderTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task Api_responses_carry_the_runtime_clock_also_when_tests_move_it()
    {
        var (admin, _, viewer) = await SignInAllAsync();

        var before = DateTimeOffset.UtcNow;
        using (var status = await viewer.GetAsync("/api/system/status", Ct))
        {
            await status.ShouldHaveStatusAsync(HttpStatusCode.OK);
            var reported = ServerTime(status);
            reported.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5));
            reported.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(5));
        }

        using (var advance = await admin.PostAsJsonAsync("/api/_test/clock", new { advanceSeconds = 3600 }, Ct))
        {
            await advance.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        using var later = await viewer.GetAsync("/api/system/status", Ct);
        ServerTime(later).ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(59), "history and last-seen times use this clock");

        using var problem = await viewer.GetAsync($"/api/devices/{Guid.NewGuid()}", Ct);
        await problem.ShouldBeProblemAsync(HttpStatusCode.NotFound, "devices.not_found");
        problem.Headers.Contains(ServerTimeHeaderMiddleware.HeaderName).ShouldBeTrue("errors carry it too");

        using var page = await viewer.GetAsync("/health/live", Ct);
        page.Headers.Contains(ServerTimeHeaderMiddleware.HeaderName).ShouldBeFalse("only the API says it");
    }

    private static DateTimeOffset ServerTime(HttpResponseMessage response) =>
        DateTimeOffset.Parse(
            response.Headers.GetValues(ServerTimeHeaderMiddleware.HeaderName).ShouldHaveSingleItem(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
