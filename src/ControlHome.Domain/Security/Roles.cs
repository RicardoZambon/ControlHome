namespace ControlHome.Domain.Security;

/// <summary>The household roles. Admin: configuration and users; Member: control; Viewer: read-only.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Viewer = "Viewer";

    /// <summary>Every role, most privileged first.</summary>
    public static IReadOnlyList<string> All { get; } = [Admin, Member, Viewer];
}
