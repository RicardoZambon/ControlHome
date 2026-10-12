using System.Text.Json.Serialization;

namespace ControlHome.Api.Http;

public static class JsonSetup
{
    /// <summary>
    /// Web defaults (camelCase) with strict numbers: numbers are never accepted as strings, which also keeps the OpenAPI
    /// schema (and the generated TypeScript types) to plain <c>number</c>.
    /// </summary>
    public static IServiceCollection AddControlHomeJson(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
}
