using System.Net;
using ControlHome.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.TestSupport;

public static class ApiFactoryExtensions
{
    /// <summary>
    /// A client that behaves like the SPA: same-origin <c>Origin</c> header, <c>X-ControlHome: 1</c>, cookies kept,
    /// redirects not followed.
    /// </summary>
    public static HttpClient CreateApiClient(this WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        client.DefaultRequestHeaders.Add("X-ControlHome", "1");
        return client;
    }

    /// <summary>Polls <c>/health/ready</c> until it answers 200; fails fast if the database initialization failed.</summary>
    public static async Task WaitUntilReadyAsync(this WebApplicationFactory<Program> factory, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var client = factory.CreateClient();
        var readiness = factory.Services.GetRequiredService<DatabaseReadiness>();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                using (var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), timeoutSource.Token))
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }

                if (readiness.State == DatabaseState.Failed)
                {
                    throw new InvalidOperationException($"Database initialization failed: {readiness.LastError}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutSource.Token);
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"/health/ready did not answer 200 within {timeout} (database state {readiness.State}).");
        }
    }
}
