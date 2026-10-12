using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.Features.Testing;
using ControlHome.Api.Hosting;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ControlHome.Api.IntegrationTests;

public sealed class ProblemDetailsTests(ControlHomeApiFactory factory) : IClassFixture<ControlHomeApiFactory>
{
    [Fact]
    public async Task Not_found_carries_code_and_trace_id()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await response.ReadJsonAsync(TestContext.Current.CancellationToken);
        problem.GetProperty("status").GetInt32().ShouldBe(404);
        problem.GetProperty("code").GetString().ShouldBe("request.not_found");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "auth.required")]
    [InlineData(StatusCodes.Status403Forbidden, "auth.forbidden")]
    [InlineData(StatusCodes.Status404NotFound, "request.not_found")]
    [InlineData(StatusCodes.Status405MethodNotAllowed, "request.method_not_allowed")]
    [InlineData(StatusCodes.Status413PayloadTooLarge, "request.too_large")]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, "request.invalid")]
    [InlineData(StatusCodes.Status429TooManyRequests, "request.rate_limited")]
    [InlineData(StatusCodes.Status503ServiceUnavailable, "internal.error")]
    public void Problem_without_a_code_gets_one_from_its_status(int status, string code)
    {
        ProblemDetailsSetup.DefaultCode(status, new ProblemDetails { Status = status }).ShouldBe(code);
    }

    [Fact]
    public async Task Unhandled_exception_is_a_bare_500_internal_error_outside_development()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync("/api/_test/throw", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(TestingEndpoints.ExceptionMarker);
        body.ShouldNotContain(nameof(InvalidOperationException));
        body.ShouldNotContain("   at ");
        var problem = await response.ReadJsonAsync(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("internal.error");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Anonymous_request_to_a_protected_endpoint_is_401_auth_required_not_a_redirect()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync("/api/users", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("auth.required");
    }

    [Fact]
    public async Task Validation_failure_is_400_validation_failed_with_camel_case_errors()
    {
        using var client = factory.CreateApiClient();
        var invalid = new
        {
            setupCode = "",
            admin = new { userName = "x", displayName = "Admin", password = "short" },
            home = new
            {
                name = "Home",
                timeZoneId = "Not/AZone",
                latitude = 91.0,
                longitude = 0.0,
                locale = "fr",
                currency = "BRL",
                pricePerKwh = (decimal?)null,
            },
        };

        using var response = await client.PostAsJsonAsync("/api/setup/complete", invalid, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.ReadJsonAsync(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("validation.failed");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        var errors = problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToArray();
        errors.ShouldBe(
            ["setupCode", "admin.userName", "admin.password", "home.timeZoneId", "home.latitude", "home.locale"],
            ignoreOrder: true);
    }
}
