using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Realtime;
using ControlHome.Application.Security;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>The safety policy end to end: roles × normal/safety-critical commands, step-up PIN, lockout.</summary>
[Collection(DevicesCollection.Name)]
public sealed class SafetyTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task Authorization_matrix_for_normal_safety_critical_and_fail_safe_commands()
    {
        var (admin, member, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var devices = await WaitForDevicesAsync(viewer, 5);
        var light = MainEndpoint(DeviceOfClass(devices, DeviceClasses.Light)).Id;
        var door = MainEndpoint(DeviceOfClass(devices, DeviceClasses.Lock)).Id;

        // Viewer: never.
        using (var response = await SendCommandAsync(viewer, light, CapabilityIds.Switch, "toggle"))
        {
            await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        }

        using (var response = await SendCommandAsync(viewer, door, CapabilityIds.Lock, "unlock"))
        {
            await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        }

        foreach (var (client, password) in new[] { (member, Password), (admin, AuthTestExtensions.AdminPassword) })
        {
            // Normal and fail-safe commands: no PIN needed.
            await SendAcceptedAsync(client, light, CapabilityIds.Switch, "toggle");
            await SendAcceptedAsync(client, door, CapabilityIds.Lock, "lock");

            // Safety-critical: a step-up first.
            using (var response = await SendCommandAsync(client, door, CapabilityIds.Lock, "unlock"))
            {
                await response.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "safety.step_up_required");
            }

            await SetPinAndStepUpAsync(client, password);
            var unlocked = await SendAcceptedAsync(client, door, CapabilityIds.Lock, "unlock");
            await Factory.Realtime.WaitForCommandAsync(unlocked, CommandStatuses.Confirmed);
        }
    }

    [Fact]
    public async Task A_step_up_grant_is_used_by_exactly_one_command()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var door = MainEndpoint(DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Lock)).Id;
        await SetPinAndStepUpAsync(member);

        await SendAcceptedAsync(member, door, CapabilityIds.Lock, "unlock");
        using var second = await SendCommandAsync(member, door, CapabilityIds.Lock, "unlock");

        await second.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "safety.step_up_required");
        (await AuditEntriesAsync("safety.step_up.succeeded")).ShouldHaveSingleItem().ActorName.ShouldBe("member");
    }

    [Fact]
    public async Task A_refused_safety_command_keeps_its_key_free_for_the_retry_after_the_step_up()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, "spy");
        var door = MainEndpoint(DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Lock)).Id;
        var key = Guid.NewGuid().ToString("N");

        using (var refused = await SendCommandAsync(member, door, CapabilityIds.Lock, "unlock", idempotencyKey: key))
        {
            await refused.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "safety.step_up_required");
        }

        await SetPinAndStepUpAsync(member);
        var commandId = await SendAcceptedAsync(member, door, CapabilityIds.Lock, "unlock", idempotencyKey: key);

        await Factory.Realtime.WaitForCommandAsync(commandId, CommandStatuses.Confirmed);
        Factory.Spy.Batches.ShouldHaveSingleItem().IdempotencyKey.ShouldBe(key);
        using var replay = await SendCommandAsync(member, door, CapabilityIds.Lock, "unlock", idempotencyKey: key);
        await replay.ShouldBeProblemAsync(HttpStatusCode.Conflict, "commands.duplicate");
    }

    [Fact]
    public async Task Safety_commands_are_audited_without_their_arguments()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var door = MainEndpoint(DeviceOfClass(await WaitForDevicesAsync(member, 5), DeviceClasses.Lock)).Id;
        await SetPinAndStepUpAsync(member);

        var commandId = await SendAcceptedAsync(member, door, CapabilityIds.Lock, "unlock");

        await Factory.Realtime.WaitForCommandAsync(commandId, CommandStatuses.Confirmed);
        await RecordingRealtimePublisher.WaitUntilAsync(async () => (await AuditEntriesAsync("commands.executed")).Count == 1, "audited");
        var audit = (await AuditEntriesAsync("commands.executed")).Single();
        audit.DetailsJson!.ShouldContain("\"command\":\"unlock\"");
        audit.DetailsJson!.ShouldNotContain("\"arguments\":{");
    }

    [Fact]
    public async Task Step_up_needs_a_pin_and_the_right_one()
    {
        var (_, member, _) = await SignInAllAsync();

        using (var noPin = await StepUpAsync(member, "1234"))
        {
            await noPin.ShouldBeProblemAsync(HttpStatusCode.Conflict, "safety.pin_not_set");
        }

        (await member.GetFromJsonAsync<SafetyPinStatusDto>("/api/auth/me/pin", Ct))!.ShouldBe(new SafetyPinStatusDto(false, null));
        using (var set = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, Password), Ct))
        {
            await set.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        (await member.GetFromJsonAsync<SafetyPinStatusDto>("/api/auth/me/pin", Ct))!.IsSet.ShouldBeTrue();
        using (var wrong = await StepUpAsync(member, "9999"))
        {
            await wrong.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "safety.pin_incorrect");
        }

        using (var malformed = await StepUpAsync(member, "12ab"))
        {
            await malformed.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        }

        await StepUpAsync(member, Pin, HttpStatusCode.NoContent);
        (await AuditEntriesAsync("safety.step_up.failed")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Changing_the_pin_needs_the_current_one()
    {
        var (_, member, _) = await SignInAllAsync();
        using (var set = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, Password), Ct))
        {
            await set.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        using (var withoutCurrent = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, "55555"), Ct))
        {
            await withoutCurrent.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "safety.password_required");
        }

        using (var wrongCurrent = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest("0000", "55555"), Ct))
        {
            await wrongCurrent.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "safety.pin_incorrect");
        }

        using (var tooShort = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(Pin, "123"), Ct))
        {
            await tooShort.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        }

        using (var changed = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(Pin, "55555"), Ct))
        {
            await changed.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        await StepUpAsync(member, "55555", HttpStatusCode.NoContent);
        (await AuditEntriesAsync("safety.pin.set")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_user_out_of_safety_commands_only()
    {
        var (admin, member, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var devices = await WaitForDevicesAsync(member, 5);
        var door = MainEndpoint(DeviceOfClass(devices, DeviceClasses.Lock)).Id;
        var light = MainEndpoint(DeviceOfClass(devices, DeviceClasses.Light)).Id;
        using (var set = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, Password), Ct))
        {
            await set.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        for (var i = 0; i < SafetyGrantStore.MaxFailures - 1; i++)
        {
            using var wrong = await StepUpAsync(member, "0000");
            await wrong.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "safety.pin_incorrect");
        }

        using (var fifth = await StepUpAsync(member, "0000"))
        {
            await fifth.ShouldBeProblemAsync((HttpStatusCode)423, "safety.locked");
            (await fifth.BodyAsync()).GetProperty("retryAfterSeconds").GetInt32().ShouldBe(15 * 60);
        }

        using (var right = await StepUpAsync(member, Pin))
        {
            await right.ShouldBeProblemAsync((HttpStatusCode)423, "safety.locked");
        }

        using (var unlock = await SendCommandAsync(member, door, CapabilityIds.Lock, "unlock"))
        {
            await unlock.ShouldBeProblemAsync((HttpStatusCode)423, "safety.locked");
        }

        await SendAcceptedAsync(member, light, CapabilityIds.Switch, "toggle");
        await SendAcceptedAsync(member, door, CapabilityIds.Lock, "lock");
        (await AuditEntriesAsync("safety.step_up.locked")).ShouldHaveSingleItem();

        // Other users are not affected.
        await SetPinAndStepUpAsync(admin, AuthTestExtensions.AdminPassword);
    }

    [Fact]
    public async Task The_first_pin_needs_the_account_password()
    {
        var (_, member, _) = await SignInAllAsync();

        using (var withoutPassword = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin), Ct))
        {
            await withoutPassword.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "safety.password_required");
        }

        using (var wrongPassword = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, "not the password"), Ct))
        {
            await wrongPassword.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "auth.password_incorrect");
        }

        (await member.GetFromJsonAsync<SafetyPinStatusDto>("/api/auth/me/pin", Ct))!.IsSet.ShouldBeFalse();
        (await AuditEntriesAsync("auth.password.verify_failed")).ShouldHaveSingleItem().DetailsJson!.ShouldContain("safety_pin");

        using (var right = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, Password), Ct))
        {
            await right.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        await StepUpAsync(member, Pin, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Wrong_passwords_for_the_pin_count_towards_the_account_lockout()
    {
        var (_, member, _) = await SignInAllAsync();
        for (var i = 1; i < 5; i++)
        {
            using var wrong = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, $"wrong password {i}"), Ct);
            await wrong.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "auth.password_incorrect");
        }

        using (var fifth = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, "wrong password 5"), Ct))
        {
            await fifth.ShouldBeProblemAsync((HttpStatusCode)423, "auth.locked_out");
        }

        // The same lockout as sign-in: even the right password is refused now, here and at the login.
        using (var right = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, Password), Ct))
        {
            await right.ShouldBeProblemAsync((HttpStatusCode)423, "auth.locked_out");
        }

        using var login = Factory.CreateApiClient();
        using var signIn = await login.PostAsJsonAsync("/api/auth/login", new { userName = "member", password = Password, rememberMe = false }, Ct);
        await signIn.ShouldBeProblemAsync((HttpStatusCode)423, "auth.locked_out");
    }

    [Fact]
    public async Task A_forgotten_pin_is_replaced_with_the_account_password()
    {
        var (_, member, _) = await SignInAllAsync();
        await SetPinAndStepUpAsync(member);

        using (var replaced = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, "86420", Password), Ct))
        {
            await replaced.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        await StepUpAsync(member, "86420", HttpStatusCode.NoContent);
        (await AuditEntriesAsync("safety.pin.set"))[^1].DetailsJson!.ShouldContain("\"via\":\"password\"");
    }

    [Fact]
    public async Task An_admin_reset_clears_the_pin_and_the_safety_lockout()
    {
        var (admin, member, _) = await SignInAllAsync();
        await SetPinAndStepUpAsync(member);
        for (var i = 0; i < SafetyGrantStore.MaxFailures; i++)
        {
            using var wrong = await StepUpAsync(member, "0000");
        }

        (await member.GetFromJsonAsync<SafetyPinStatusDto>("/api/auth/me/pin", Ct))!.LockedForSeconds.ShouldNotBeNull();
        var memberId = await UserIdAsync(admin, "member");

        using (var notAdmin = await member.DeleteAsync($"/api/users/{memberId}/safety-pin", Ct))
        {
            await notAdmin.ShouldHaveStatusAsync(HttpStatusCode.Forbidden);
        }

        using (var reset = await admin.DeleteAsync($"/api/users/{memberId}/safety-pin", Ct))
        {
            await reset.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        (await member.GetFromJsonAsync<SafetyPinStatusDto>("/api/auth/me/pin", Ct))!.ShouldBe(new SafetyPinStatusDto(false, null));
        using (var noPin = await StepUpAsync(member, Pin))
        {
            await noPin.ShouldBeProblemAsync(HttpStatusCode.Conflict, "safety.pin_not_set");
        }

        (await AuditEntriesAsync("safety.pin.reset")).ShouldHaveSingleItem().ActorName.ShouldBe(AuthTestExtensions.AdminUserName);
        await SetPinAndStepUpAsync(member);
    }

    [Fact]
    public async Task Concurrent_wrong_pins_never_get_past_the_lockout()
    {
        var (_, member, _) = await SignInAllAsync();
        using (var set = await member.PutAsJsonAsync("/api/auth/me/pin", new SetPinRequest(null, Pin, Password), Ct))
        {
            await set.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => StepUpAsync(member, (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var codes = new List<string?>();
        foreach (var response in responses)
        {
            using (response)
            {
                codes.Add((await response.BodyAsync()).GetProperty("code").GetString());
            }
        }

        // One check at a time: exactly MaxFailures - 1 wrong PINs are answered, the 5th starts the lockout, the rest are refused.
        codes.Count(c => c == "safety.pin_incorrect").ShouldBe(SafetyGrantStore.MaxFailures - 1);
        codes.Count(c => c == "safety.locked").ShouldBe(20 - (SafetyGrantStore.MaxFailures - 1));
        (await AuditEntriesAsync("safety.step_up.failed")).Count.ShouldBe(SafetyGrantStore.MaxFailures);

        // The right PIN does not get through the lockout either.
        using var right = await StepUpAsync(member, Pin);
        await right.ShouldBeProblemAsync((HttpStatusCode)423, "safety.locked");
    }
}
