using System.Security.Claims;
using ControlHome.Application.Abstractions;
using ControlHome.Domain.Auditing;

namespace ControlHome.Api.Hosting;

/// <summary><see cref="ICurrentActor"/> from the current HTTP request (System when there is none).</summary>
internal sealed class CurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public AuditActorType Type => accessor.HttpContext switch
    {
        null => AuditActorType.System,
        { User.Identity.IsAuthenticated: true } => AuditActorType.User,
        _ => AuditActorType.Anonymous,
    };

    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? UserName => accessor.HttpContext?.User.Identity?.IsAuthenticated == true
        ? accessor.HttpContext.User.Identity.Name
        : null;

    public string? IpAddress
    {
        get
        {
            var address = accessor.HttpContext?.Connection.RemoteIpAddress;
            if (address is null)
            {
                return null;
            }

            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }
    }
}
