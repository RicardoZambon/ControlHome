using System.Net;
using System.Net.Http.Json;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests;

/// <summary>Review focus 1: racing completions create exactly one admin (sp_getapplock 'ControlHome.Setup').</summary>
[Collection(AuthCollection.Name)]
public sealed class SetupConcurrencyTests(AuthApiFactory factory)
    : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const int Rounds = 3;

    [Fact]
    public async Task Two_simultaneous_completions_give_exactly_one_200_and_one_409()
    {
        for (var round = 0; round < Rounds; round++)
        {
            if (round > 0)
            {
                await Factory.ResetAppAsync(Ct);
            }

            using var first = Factory.CreateApiClient();
            using var second = Factory.CreateApiClient();
            using var start = new SemaphoreSlim(0, 2);

            // Different user names, so only the setup lock (not a unique index) can stop the second admin.
            var requests = new[]
            {
                PostWhenReleasedAsync(first, "admin1", start),
                PostWhenReleasedAsync(second, "admin2", start),
            };
            start.Release(2);
            var responses = await Task.WhenAll(requests);
            try
            {
                responses.Select(r => r.StatusCode).ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict], ignoreOrder: true);
                var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
                (await conflict.ReadProblemCodeAsync(Ct)).ShouldBe("setup.already_completed");

                (await QueryDbAsync(db => db.Users.CountAsync(Ct))).ShouldBe(1);
                (await QueryDbAsync(db => db.UserRoles.CountAsync(Ct))).ShouldBe(1);
                (await QueryDbAsync(db => db.Homes.CountAsync(Ct))).ShouldBe(1);
                (await AuditEntriesAsync("setup.completed")).Count.ShouldBe(1);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }

    private static async Task<HttpResponseMessage> PostWhenReleasedAsync(HttpClient client, string userName, SemaphoreSlim start)
    {
        await start.WaitAsync(Ct);
        return await client.PostAsJsonAsync("/api/setup/complete", AuthTestExtensions.SetupRequest(userName: userName), Ct);
    }
}
