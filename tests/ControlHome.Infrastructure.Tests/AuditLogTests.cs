using System.Security.Cryptography;
using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Domain.Auditing;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace ControlHome.Infrastructure.Tests;

public sealed class AuditLogTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Writes_the_entry_with_actor_ip_time_and_json_details()
    {
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        var suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var userId = Guid.CreateVersion7();
        var actor = Substitute.For<ICurrentActor>();
        actor.Type.Returns(AuditActorType.User);
        actor.UserId.Returns(userId);
        actor.UserName.Returns("admin");
        actor.IpAddress.Returns("192.168.0.10");

        await using var services = InfrastructureHost.Build(
            new Dictionary<string, string?> { ["ConnectionStrings:ControlHome"] = sql.CreateDatabaseConnectionString(suffix) },
            s =>
            {
                s.AddSingleton<TimeProvider>(new FakeTimeProvider(now));
                s.AddSingleton(actor);
            });
        try
        {
            var initializer = services.GetRequiredService<DatabaseInitializer>();
            await initializer.StartAsync(ct);
            await initializer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(120), ct);
            services.GetRequiredService<DatabaseReadiness>().State.ShouldBe(DatabaseState.Ready);

            await using (var scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IAuditLog>()
                    .WriteAsync(new AuditEvent("users.created", "user", "42", new { Role = "Member", Fields = new[] { "displayName" } }), ct);
            }

            await using (var scope = services.CreateAsyncScope())
            {
                var entry = await scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>().AuditLog.SingleAsync(ct);
                entry.Action.ShouldBe("users.created");
                entry.OccurredAtUtc.ShouldBe(now);
                entry.ActorType.ShouldBe(AuditActorType.User);
                entry.ActorId.ShouldBe(userId.ToString());
                entry.ActorName.ShouldBe("admin");
                entry.IpAddress.ShouldBe("192.168.0.10");
                entry.TargetType.ShouldBe("user");
                entry.TargetId.ShouldBe("42");
                using var details = JsonDocument.Parse(entry.DetailsJson!);
                details.RootElement.GetProperty("role").GetString().ShouldBe("Member");
                details.RootElement.GetProperty("fields")[0].GetString().ShouldBe("displayName");
            }

            await initializer.StopAsync(ct);
        }
        finally
        {
            await sql.DropDatabaseAsync(sql.DatabaseName(suffix));
        }
    }
}
