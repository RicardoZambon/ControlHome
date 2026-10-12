using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Floors;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests.Floors;

/// <summary>Floor and room helpers for the integration tests.</summary>
internal static class FloorApi
{
    public static PointCmDto P(double x, double y) => new(x, y);

    /// <summary>An axis-aligned square with its top-left corner at (x, y).</summary>
    public static PointCmDto[] Square(double x, double y, double size) =>
        [P(x, y), P(x + size, y), P(x + size, y + size), P(x, y + size)];

    public static RoomInput Room(string name, IReadOnlyList<PointCmDto> polygon, int? id = null, string icon = "sofa", string colorToken = "room-1") =>
        new(id, name, icon, colorToken, polygon);

    public static string FloorUrl(int id) => string.Create(CultureInfo.InvariantCulture, $"/api/floors/{id}");

    public static string LayoutUrl(int id) => string.Create(CultureInfo.InvariantCulture, $"/api/floors/{id}/layout");

    public static async Task<FloorDto> CreateFloorAsync(
        this HttpClient client,
        CancellationToken cancellationToken,
        string name = "Térreo",
        int level = 0,
        int? widthCm = null,
        int? heightCm = null)
    {
        using var response = await client.PostAsJsonAsync("/api/floors", new CreateFloorRequest(name, level, widthCm, heightCm), cancellationToken);
        await response.ShouldHaveStatusAsync(HttpStatusCode.Created, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<FloorDto>(cancellationToken))!;
    }

    public static async Task<FloorDto> GetFloorAsync(this HttpClient client, int id, CancellationToken cancellationToken) =>
        (await client.GetFromJsonAsync<FloorDto>(FloorUrl(id), cancellationToken))!;

    public static Task<HttpResponseMessage> SaveLayoutAsync(
        this HttpClient client,
        int floorId,
        string rowVersion,
        IReadOnlyList<RoomInput> rooms,
        CancellationToken cancellationToken) =>
        client.PutAsJsonAsync(LayoutUrl(floorId), new SaveFloorLayoutRequest(rooms, rowVersion), cancellationToken);

    /// <summary>Saves a layout that must succeed and returns the updated floor.</summary>
    public static async Task<FloorDto> SaveLayoutOkAsync(
        this HttpClient client,
        FloorDto floor,
        IReadOnlyList<RoomInput> rooms,
        CancellationToken cancellationToken)
    {
        using var response = await client.SaveLayoutAsync(floor.Id, floor.RowVersion, rooms, cancellationToken);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<FloorDto>(cancellationToken))!;
    }

    public static async Task ShouldHaveStatusAsync(this HttpResponseMessage response, HttpStatusCode expected, CancellationToken cancellationToken)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.StatusCode.ShouldBe(expected, body);
        }
    }

    /// <summary>A problem's field errors (<c>errors</c>) as path → messages.</summary>
    public static async Task<Dictionary<string, string[]>> ReadFieldErrorsAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = await response.ReadJsonAsync(cancellationToken);
        json.GetProperty("code").GetString().ShouldBe("validation.failed");
        return json.GetProperty("errors").EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToArray(),
            StringComparer.Ordinal);
    }
}
