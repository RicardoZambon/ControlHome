using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.Features.Testing;
using ControlHome.TestSupport;

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
    [InlineData("GET", "/api/auth/login", "POST")]
    [InlineData("DELETE", "/api/home", "GET, PUT")]
    [InlineData("PATCH", "/api/home", "GET, PUT")]
    [InlineData("GET", "/api/users/0199a3b2-0000-7000-8000-000000000001", "DELETE, PUT")]
    [InlineData("GET", "/api/users/0199a3b2-0000-7000-8000-000000000001/password", "POST")]
    public async Task Wrong_method_on_a_known_path_is_405_with_allow(string method, string path, string allow)
    {
        using var client = factory.CreateApiClient(); // same-origin headers, so mutations pass the request edge

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json"); // never index.html
        response.Content.Headers.Allow.ShouldBe(allow.Split(", "));
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.method_not_allowed");
    }

    [Theory]
    [InlineData("POST", "/api/nope")]
    [InlineData("GET", "/api/users/not-a-guid")] // the route constraint is respected: no such path
    [InlineData("DELETE", "/api/users/not-a-guid")]
    [InlineData("GET", "/api/auth/login/extra")]
    public async Task Unknown_path_is_still_404_whatever_the_method(string method, string path)
    {
        using var client = factory.CreateApiClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.Allow.ShouldBeEmpty();
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.not_found");
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
