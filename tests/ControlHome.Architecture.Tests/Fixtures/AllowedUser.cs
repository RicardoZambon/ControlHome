// Positive fixture: what Application is allowed to do, i.e. use ASP.NET Core Identity's model types.
namespace ControlHome.Architecture.Tests.Fixtures.IdentityModel;

public sealed class AllowedUser : Microsoft.AspNetCore.Identity.IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public static Type ManagerType => typeof(Microsoft.AspNetCore.Identity.UserManager<AllowedUser>);
}
