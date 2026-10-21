using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ControlHome.Api.IntegrationTests.Files;
using ControlHome.Api.IntegrationTests.Floors;
using ControlHome.Application.HomeBackup;
using ControlHome.Application.Floors;
using ControlHome.Application.Integrations;
using ControlHome.Application.Users;
using ControlHome.Infrastructure.HomeBackup;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>Backups: export (secrets only with a passphrase), import into an empty home, the daily schedule.</summary>
[Collection(DevicesCollection.Name)]
public sealed class BackupTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    private const string BackupPassphrase = "a long backup passphrase";

    [Fact]
    public async Task A_backup_restores_the_home_into_a_fresh_installation()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        using (var seed = await admin.PostAsync("/api/_test/seed-demo?locale=en", null, Ct))
        {
            await seed.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        var token = $"CANARY-{Guid.NewGuid():N}";
        var canary = await CreateIntegrationAsync(admin, CanaryConnectorFactory.Key, $$"""{"token":"{{token}}","label":"porch"}""");
        var devicesBefore = await WaitForDevicesAsync(viewer, 21);
        var floorsBefore = (await admin.GetFromJsonAsync<FloorSummaryDto[]>("/api/floors", Ct))!;
        var ground = await admin.GetFloorAsync(floorsBefore[0].Id, Ct);
        var image = await admin.UploadAsync(TestImages.Png(64, 48), "plan.png", Ct);
        using (var background = await admin.PutAsJsonAsync(
            FloorApi.FloorUrl(ground.Id),
            new UpdateFloorRequest(ground.Name, ground.Level, ground.WidthCm, ground.HeightCm, ground.GridCm, new FloorBackgroundDto(image.Id, 0.5, 0, 0, 2), ground.RowVersion),
            Ct))
        {
            await background.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        // Without a passphrase: no secrets.
        using var plain = await admin.GetAsync("/api/backup/export", Ct);
        await plain.ShouldHaveStatusAsync(HttpStatusCode.OK);
        plain.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        plain.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldStartWith("controlhome-backup-");
        var plainEntries = Entries(await plain.Content.ReadAsByteArrayAsync(Ct));
        plainEntries.Keys.ShouldBe(
            ["manifest.json", "home.json", "floors.json", "files.json", $"files/{image.Id}.webp", "integrations.json", "devices.json", "endpoints.json", "placements.json", "users.json"],
            ignoreOrder: true);
        var manifest = JsonDocument.Parse(plainEntries["manifest.json"]).RootElement;
        (manifest.GetProperty("schemaVersion").GetInt32(), manifest.GetProperty("includesSecrets").GetBoolean()).ShouldBe((1, false));
        manifest.GetProperty("counts").GetProperty("devices").GetInt32().ShouldBe(21);
        plainEntries["users.json"].ShouldNotContain("hash", Case.Insensitive);
        plainEntries.Values.ShouldAllBe(text => !text.Contains(token, StringComparison.Ordinal));

        // With a passphrase: secrets encrypted, never in clear.
        using var withSecrets = await admin.PostAsJsonAsync("/api/backup/export", new ExportBackupRequest(BackupPassphrase), Ct);
        await withSecrets.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var archive = await withSecrets.Content.ReadAsByteArrayAsync(Ct);
        var entries = Entries(archive);
        entries.Values.ShouldAllBe(text => !text.Contains(token, StringComparison.Ordinal));
        JsonDocument.Parse(entries["manifest.json"]).RootElement.GetProperty("includesSecrets").GetBoolean().ShouldBeTrue();
        (await AuditEntriesAsync("backup.exported")).Count.ShouldBe(2);

        // A fresh installation: import into the empty home.
        await Factory.ResetAppAsync(Ct);
        using var fresh = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using var imported = await ImportAsync(fresh, archive, BackupPassphrase);
        await imported.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var result = (await imported.Content.ReadFromJsonAsync<BackupImportResultDto>(Ct))!;

        result.ShouldBe(new BackupImportResultDto(2, 8, 1, 2, 21, DemoPlacementCount, UsersCreated: 2, SecretsRestored: true));
        var devicesAfter = await WaitForDevicesAsync(fresh, 21);
        devicesAfter.Select(d => (d.Id, d.Name)).ShouldBe(devicesBefore.Select(d => (d.Id, d.Name)), ignoreOrder: true);
        devicesAfter.SelectMany(d => d.Endpoints).Count(e => e.Favorite).ShouldBe(6);
        var floorsAfter = (await fresh.GetFromJsonAsync<FloorSummaryDto[]>("/api/floors", Ct))!;
        floorsAfter.Select(f => (f.Name, f.Level, f.RoomCount)).ShouldBe(floorsBefore.Select(f => (f.Name, f.Level, f.RoomCount)));
        var groundAfter = await fresh.GetFloorAsync(floorsAfter[0].Id, Ct);
        groundAfter.Background!.FileId.ShouldBe(image.Id);
        groundAfter.Rooms.Select(r => (r.Name, r.AreaM2)).ShouldBe(ground.Rooms.Select(r => (r.Name, r.AreaM2)));
        groundAfter.Placements.Count.ShouldBe(ground.Placements.Count);
        (await fresh.GetAsync($"/api/files/{image.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var users = (await fresh.GetFromJsonAsync<UserDto[]>("/api/users", Ct))!;
        users.Select(u => u.UserName).ShouldBe(["admin", "member", "viewer"], ignoreOrder: true);
        var saved = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == canary.Id, Ct));
        Factory.Services.GetRequiredService<IIntegrationSecretProtector>().Read(saved.SecretsProtected).Values["token"].ShouldBe(token);
        (await AuditEntriesAsync("backup.imported")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Imports_are_refused_unless_the_home_is_empty_and_the_backup_opens()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, CanaryConnectorFactory.Key, """{"token":"CANARY-refusals"}""");
        using var export = await admin.PostAsJsonAsync("/api/backup/export", new ExportBackupRequest(BackupPassphrase), Ct);
        var archive = await export.Content.ReadAsByteArrayAsync(Ct);

        using (var notEmpty = await ImportAsync(admin, archive, BackupPassphrase))
        {
            await notEmpty.ShouldBeProblemAsync(HttpStatusCode.Conflict, "backup.not_empty");
        }

        using (var byMember = await ImportAsync(member, archive, BackupPassphrase))
        {
            await byMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        }

        await Factory.ResetAppAsync(Ct);
        using var fresh = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using (var garbage = await ImportAsync(fresh, Encoding.UTF8.GetBytes("not a zip"), null))
        {
            await garbage.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "backup.invalid");
        }

        using (var noPassphrase = await ImportAsync(fresh, archive, null))
        {
            await noPassphrase.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "backup.passphrase_required");
        }

        using (var wrong = await ImportAsync(fresh, archive, "another passphrase entirely"))
        {
            await wrong.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "backup.passphrase_incorrect");
        }

        using (var newer = await ImportAsync(fresh, WithManifest(archive, m => m.SetSchemaVersion(99)), BackupPassphrase))
        {
            await newer.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "backup.unsupported_version");
        }

        using (var damaged = await ImportAsync(fresh, WithEntry(archive, "devices.json", "[{\"id\":42}]"), BackupPassphrase))
        {
            await damaged.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "backup.invalid");
        }

        using (var shortPassphrase = await fresh.PostAsJsonAsync("/api/backup/export", new ExportBackupRequest("short"), Ct))
        {
            await shortPassphrase.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        }

        (await QueryDbAsync(db => db.Integrations.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task A_backup_is_written_daily_and_seven_are_kept()
    {
        var (admin, _, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var scheduler = Factory.Services.GetRequiredService<BackupScheduler>();
        foreach (var old in scheduler.Existing())
        {
            File.Delete(old.Path);
        }

        var first = await scheduler.RunOnceAsync(force: false, Ct);
        first.ShouldNotBeNull();
        Entries(await File.ReadAllBytesAsync(first, Ct)).Keys.ShouldContain("manifest.json");
        (await scheduler.RunOnceAsync(force: false, Ct)).ShouldBeNull("the next one is due in a day");

        var folder = Path.GetDirectoryName(first)!;
        for (var day = 1; day <= 8; day++)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, $"controlhome-backup-2020010{day}-030000.zip"), [], Ct);
        }

        (await scheduler.RunOnceAsync(force: true, Ct)).ShouldNotBeNull();
        var kept = scheduler.Existing();
        kept.Count.ShouldBe(7);
        kept.Select(k => Path.GetFileName(k.Path)).ShouldContain(Path.GetFileName(first));
        kept.Select(k => k.WrittenAtUtc).ShouldBeInOrder(SortDirection.Descending);
    }

    /// <summary>The demo home's placements (22) plus none for the canary.</summary>
    private const int DemoPlacementCount = 22;

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, byte[] archive, string? passphrase)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "backup.zip");
        if (passphrase is not null)
        {
            form.Add(new StringContent(passphrase), "passphrase");
        }

        return await client.PostAsync("/api/backup/import", form, Ct);
    }

    /// <summary>Every entry of a zip archive as text.</summary>
    private static Dictionary<string, string> Entries(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var reader = new StreamReader(e.Open(), Encoding.Latin1);
                return reader.ReadToEnd();
            });
    }

    private static byte[] WithManifest(byte[] archive, Action<ManifestEditor> edit)
    {
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(Entries(archive)["manifest.json"])!.AsObject();
        edit(new ManifestEditor(manifest));
        return WithEntry(archive, "manifest.json", manifest.ToJsonString());
    }

    private static byte[] WithEntry(byte[] archive, string name, string content)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName);
                using var to = copy.Open();
                if (entry.FullName == name)
                {
                    to.Write(Encoding.UTF8.GetBytes(content));
                }
                else
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                }
            }
        }

        return output.ToArray();
    }

    private sealed class ManifestEditor(System.Text.Json.Nodes.JsonObject manifest)
    {
        public void SetSchemaVersion(int version) => manifest["schemaVersion"] = version;
    }
}
