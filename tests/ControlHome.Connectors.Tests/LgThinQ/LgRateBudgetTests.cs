using System.Net;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Api;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>The token's call budget (a token bucket on the connector's clock, a pause after 1306) and the error code mapping.</summary>
public sealed class LgRateBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Five_calls_go_out_at_once_then_one_every_ten_seconds()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        using var budget = new LgRateBudget(time, LgRateOptions.Default);

        for (var i = 0; i < 5; i++)
        {
            await budget.AcquireAsync(Ct);
        }

        var sixth = budget.AcquireAsync(Ct).AsTask();
        await Task.Delay(50, Ct);
        sixth.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(50, Ct);
        sixth.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await sixth.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        budget.CallsLastHour.ShouldBe(6);
    }

    [Fact]
    public async Task Commands_go_out_before_background_calls_waiting_for_the_budget()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        using var budget = new LgRateBudget(time, new LgRateOptions(1, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1)));
        await budget.AcquireAsync(Ct);

        var poll = budget.AcquireAsync(Ct).AsTask();
        var subscribe = budget.AcquireAsync(Ct).AsTask();
        var command = budget.AcquireAsync(priority: true, Ct).AsTask();
        var second = budget.AcquireAsync(priority: true, Ct).AsTask();

        time.Advance(TimeSpan.FromSeconds(10));
        await command.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        (poll.IsCompleted, subscribe.IsCompleted, second.IsCompleted).ShouldBe((false, false, false));

        time.Advance(TimeSpan.FromSeconds(10));
        await second.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        time.Advance(TimeSpan.FromSeconds(10));
        await poll.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        subscribe.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(10));
        await subscribe.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_the_queue_without_taking_a_call()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        using var budget = new LgRateBudget(time, new LgRateOptions(1, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1)));
        await budget.AcquireAsync(Ct);
        using var cancel = new CancellationTokenSource();

        var cancelled = budget.AcquireAsync(cancel.Token).AsTask();
        var next = budget.AcquireAsync(Ct).AsTask();
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(cancelled);
        time.Advance(TimeSpan.FromSeconds(10));
        await next.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        budget.CallsLastHour.ShouldBe(2);
    }

    [Fact]
    public void A_retry_after_longer_than_a_day_is_capped()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        using var budget = new LgRateBudget(time, LgRateOptions.Default);

        budget.Block(TimeSpan.FromDays(30)).ShouldBe(time.GetUtcNow().AddDays(1));
    }

    [Fact]
    public async Task After_a_1306_every_call_is_refused_for_an_hour_without_reaching_lg()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        using var budget = new LgRateBudget(time, LgRateOptions.Default);

        var until = budget.Block();

        until.ShouldBe(time.GetUtcNow().AddHours(1));
        (await Should.ThrowAsync<LgQuotaBlockedException>(budget.AcquireAsync(Ct).AsTask())).Until.ShouldBe(until);
        time.Advance(TimeSpan.FromMinutes(61));
        await budget.AcquireAsync(Ct);
        budget.BlockedUntil.ShouldBeNull();
    }

    [Fact]
    public void Budgets_are_shared_per_token()
    {
        var time = new FakeTimeProvider();
        using var budgets = new LgRateBudgets(LgRateOptions.Default);

        budgets.For("test-pat-a", time).ShouldBeSameAs(budgets.For("test-pat-a", time));
        budgets.For("test-pat-a", time).ShouldNotBeSameAs(budgets.For("test-pat-b", time));
    }

    [Theory]
    [InlineData("1103", nameof(LgErrorKind.NeedsReauth))]
    [InlineData("1218", nameof(LgErrorKind.NeedsReauth))]
    [InlineData("1306", nameof(LgErrorKind.QuotaExceeded))]
    [InlineData("1304", nameof(LgErrorKind.Account))]
    [InlineData("1307", nameof(LgErrorKind.Account))]
    [InlineData("1207", nameof(LgErrorKind.AlreadySubscribed))]
    [InlineData("2301", nameof(LgErrorKind.CommandRejected))]
    [InlineData("1308", nameof(LgErrorKind.CommandRejected))]
    [InlineData("1309", nameof(LgErrorKind.CommandRejected))]
    [InlineData("2207", nameof(LgErrorKind.CommandRejected))]
    [InlineData("1219", nameof(LgErrorKind.Device))]
    [InlineData("2000", nameof(LgErrorKind.Transient))]
    [InlineData("9999", nameof(LgErrorKind.Other))]
    public void Error_codes_are_classified(string code, string kind) => LgErrors.Classify(code, HttpStatusCode.BadRequest).ShouldBe(Enum.Parse<LgErrorKind>(kind));

    [Fact]
    public void Http_statuses_without_a_code_are_classified()
    {
        LgErrors.Classify(null, HttpStatusCode.Unauthorized).ShouldBe(LgErrorKind.NeedsReauth);
        LgErrors.Classify(null, HttpStatusCode.TooManyRequests).ShouldBe(LgErrorKind.QuotaExceeded);
        LgErrors.Classify(null, HttpStatusCode.BadGateway).ShouldBe(LgErrorKind.Transient);
        LgErrors.Classify(null, HttpStatusCode.NotFound).ShouldBe(LgErrorKind.Other);
    }

    [Theory]
    [InlineData("2301", CommandRejectionCodes.Refused, "Remote Start")]
    [InlineData("2302", CommandRejectionCodes.Refused, "current state")]
    [InlineData("2303", CommandRejectionCodes.Refused, "error")]
    [InlineData("2304", CommandRejectionCodes.Refused, "powered off")]
    [InlineData("2305", CommandRejectionCodes.Refused, "mode")]
    [InlineData("1308", CommandRejectionCodes.Refused, "not allowed to control")]
    [InlineData("1309", CommandRejectionCodes.Refused, "does not allow")]
    [InlineData("2207", CommandRejectionCodes.InvalidArguments, "several settings")]
    [InlineData("1222", CommandRejectionCodes.Unavailable, "not connected")]
    [InlineData("1103", CommandRejectionCodes.Unavailable, "access token")]
    [InlineData("1306", CommandRejectionCodes.Unavailable, "call limit")]
    public void Control_errors_become_user_readable_rejections(string code, string rejection, string text)
    {
        var outcome = LgErrors.Rejection(new LgApiException(code, HttpStatusCode.BadRequest)).ShouldBeOfType<CommandOutcome.Rejected>();

        outcome.Code.ShouldBe(rejection);
        outcome.Message.ShouldContain(text);
        outcome.Message.ShouldContain(code);
    }

    [Fact]
    public void Message_ids_are_22_url_safe_characters()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => LgHeadersHandler.NewMessageId()).ToList();

        ids.ShouldAllBe(id => id.Length == 22 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));
        ids.Distinct().Count().ShouldBe(200);
    }
}
