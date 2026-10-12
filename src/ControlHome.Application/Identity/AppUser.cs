using Microsoft.AspNetCore.Identity;

namespace ControlHome.Application.Identity;

/// <summary>A household account (ASP.NET Core Identity user with a <see cref="Guid"/> key).</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>UI language: <c>pt-BR</c> or <c>en</c>.</summary>
    public string Locale { get; set; } = "en";

    /// <summary><c>light</c>, <c>dark</c> or <c>system</c>.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>A disabled user cannot sign in; existing sessions end at the next security-stamp check.</summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }

    /// <summary>Lockouts in a row without a successful login; drives the exponential lockout back-off.</summary>
    public int ConsecutiveLockouts { get; set; }
}
