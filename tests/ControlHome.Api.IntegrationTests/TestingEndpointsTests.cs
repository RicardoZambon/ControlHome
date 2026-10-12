using System.Net;
using ControlHome.Application.Identity;
using ControlHome.Domain.Auditing;
using ControlHome.Domain.Homes;
using ControlHome.Domain.Security;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

public sealed class TestingEndpointsTests(ControlHomeApiFactory factory) : IClassFixture<ControlHomeApiFactory>
{
    [Fact]
    public async Task Reset_deletes_app_and_identity_rows_and_keeps_the_roles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Homes.Add(new Home { Name = "Test home", TimeZoneId = "America/Sao_Paulo", Locale = "pt-BR", CreatedAtUtc = now });
            db.AuditLog.Add(new AuditLogEntry { OccurredAtUtc = now, ActorType = AuditActorType.System, Action = "test.seeded" });
            var user = new AppUser
            {
                Id = Guid.CreateVersion7(),
                UserName = "reset-me",
                NormalizedUserName = "RESET-ME",
                DisplayName = "Reset me",
                SecurityStamp = Guid.NewGuid().ToString(),
                CreatedAtUtc = now,
            };
            db.Users.Add(user);
            var admin = await db.Roles.SingleAsync(r => r.Name == Roles.Admin, ct);
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = admin.Id });
            await db.SaveChangesAsync(ct);
        }

        using var client = factory.CreateApiClient();
        using var response = await client.PostAsync("/api/_test/reset", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
            (await db.Homes.CountAsync(ct)).ShouldBe(0);
            (await db.AuditLog.CountAsync(ct)).ShouldBe(0);
            (await db.Users.CountAsync(ct)).ShouldBe(0);
            (await db.UserRoles.CountAsync(ct)).ShouldBe(0);
            (await db.Roles.Select(r => r.Name).ToListAsync(ct)).ShouldBe(Roles.All, ignoreOrder: true);
        }
    }

    [Fact]
    public async Task Testing_endpoints_are_absent_from_the_openapi_document()
    {
        var document = await File.ReadAllTextAsync(OpenApiDocument.Path, TestContext.Current.CancellationToken);

        document.ShouldNotContain("/api/_test");
    }
}

public sealed class TestingEndpointsInProductionTests(ProductionApiFactory factory) : IClassFixture<ProductionApiFactory>
{
    [Theory]
    [InlineData("POST", "/api/_test/reset")]
    [InlineData("GET", "/api/_test/throw")]
    public async Task Testing_endpoints_return_404_in_production(string method, string path)
    {
        using var client = factory.CreateApiClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.not_found");
    }
}
