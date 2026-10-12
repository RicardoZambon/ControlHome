using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlHome.Infrastructure.Persistence.Configurations;

// ASP.NET Core Identity tables live in schema "auth" without the "AspNet" prefix. These run after
// IdentityDbContext.OnModelCreating, so they only rename tables and configure our extra AppUser columns.

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("Users", ControlHomeDbContext.AuthSchema);
        builder.Property(u => u.DisplayName).HasMaxLength(ValidationRules.DisplayNameMaxLength).IsRequired();
        builder.Property(u => u.Locale).HasMaxLength(16).IsRequired();
        builder.Property(u => u.Theme).HasMaxLength(16).IsRequired();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<IdentityRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRole<Guid>> builder) => builder.ToTable("Roles", ControlHomeDbContext.AuthSchema);
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder) =>
        builder.ToTable("UserRoles", ControlHomeDbContext.AuthSchema);
}

internal sealed class UserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder) =>
        builder.ToTable("UserClaims", ControlHomeDbContext.AuthSchema);
}

internal sealed class UserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder) =>
        builder.ToTable("UserLogins", ControlHomeDbContext.AuthSchema);
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder) =>
        builder.ToTable("UserTokens", ControlHomeDbContext.AuthSchema);
}

internal sealed class RoleClaimConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder) =>
        builder.ToTable("RoleClaims", ControlHomeDbContext.AuthSchema);
}
