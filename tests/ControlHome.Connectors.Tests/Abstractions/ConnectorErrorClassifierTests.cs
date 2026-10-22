using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.Abstractions;

public sealed class ConnectorErrorClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, ConnectorErrorKind.None, null)]
    [InlineData(HttpStatusCode.Unauthorized, ConnectorErrorKind.Unauthorized, IntegrationStatus.NeedsReauth)]
    [InlineData(HttpStatusCode.PaymentRequired, ConnectorErrorKind.PlanRequired, IntegrationStatus.PlanRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, ConnectorErrorKind.RateLimited, IntegrationStatus.Degraded)]
    [InlineData(HttpStatusCode.RequestTimeout, ConnectorErrorKind.Transient, IntegrationStatus.Degraded)]
    [InlineData(HttpStatusCode.InternalServerError, ConnectorErrorKind.Transient, IntegrationStatus.Degraded)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ConnectorErrorKind.Transient, IntegrationStatus.Degraded)]
    [InlineData(HttpStatusCode.Forbidden, ConnectorErrorKind.Permanent, IntegrationStatus.Error)]
    [InlineData(HttpStatusCode.NotFound, ConnectorErrorKind.Permanent, IntegrationStatus.Error)]
    public void Maps_status_codes(HttpStatusCode status, ConnectorErrorKind kind, IntegrationStatus? integrationStatus)
    {
        var error = ConnectorErrorClassifier.Classify(status);

        error.Kind.ShouldBe(kind);
        error.Status.ShouldBe(integrationStatus);
        error.Retryable.ShouldBe(kind is ConnectorErrorKind.Transient or ConnectorErrorKind.RateLimited);
    }

    [Fact]
    public void A_plan_error_in_the_body_means_plan_required_whatever_the_status()
    {
        ConnectorErrorClassifier.Classify(HttpStatusCode.Forbidden, planError: true).Status.ShouldBe(IntegrationStatus.PlanRequired);
    }

    [Fact]
    public void Rate_limits_back_off_for_retry_after_or_a_default()
    {
        ConnectorErrorClassifier.Classify(HttpStatusCode.TooManyRequests).RetryAfter.ShouldBe(ConnectorErrorClassifier.DefaultRateLimitBackoff);
        ConnectorErrorClassifier.Classify(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)).RetryAfter.ShouldBe(TimeSpan.FromSeconds(7));
        ConnectorErrorClassifier.Classify(HttpStatusCode.TooManyRequests, TimeSpan.FromDays(3)).RetryAfter.ShouldBe(ConnectorErrorClassifier.MaxRetryAfter);
    }

    [Fact]
    public void Reads_retry_after_as_seconds_or_as_a_date()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
        using var delta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        delta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        using var date = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        date.Headers.RetryAfter = new RetryConditionHeaderValue(time.GetUtcNow().AddMinutes(2));
        using var none = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        ConnectorErrorClassifier.Classify(delta, time).RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
        ConnectorErrorClassifier.Classify(date, time).RetryAfter.ShouldBe(TimeSpan.FromMinutes(2));
        ConnectorErrorClassifier.Classify(none, time).RetryAfter.ShouldBeNull();
    }

    [Fact]
    public void Network_failures_are_transient_and_bugs_are_permanent()
    {
        ConnectorErrorClassifier.Classify(new HttpRequestException("down")).Kind.ShouldBe(ConnectorErrorKind.Transient);
        ConnectorErrorClassifier.Classify(new SocketException()).Kind.ShouldBe(ConnectorErrorKind.Transient);
        ConnectorErrorClassifier.Classify(new TaskCanceledException()).Kind.ShouldBe(ConnectorErrorKind.Transient);
        ConnectorErrorClassifier.Classify(new HttpRequestException("no", null, HttpStatusCode.Unauthorized)).Status
            .ShouldBe(IntegrationStatus.NeedsReauth);
        ConnectorErrorClassifier.Classify(new InvalidOperationException()).Kind.ShouldBe(ConnectorErrorKind.Permanent);
    }
}
