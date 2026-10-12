using System.Net.Http.Json;
using System.Text.Json;

namespace ControlHome.TestSupport;

public static class HttpResponseExtensions
{
    /// <summary>The body as a <see cref="JsonElement"/> (for asserting on ProblemDetails members such as <c>code</c>).</summary>
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    /// <summary>The ProblemDetails <c>code</c> extension, or <c>null</c>.</summary>
    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        var json = await response.ReadJsonAsync(cancellationToken);
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
