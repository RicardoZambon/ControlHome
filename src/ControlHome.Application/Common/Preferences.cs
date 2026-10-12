namespace ControlHome.Application.Common;

/// <summary>Supported UI/content locales.</summary>
public static class Locales
{
    public const string PortugueseBrazil = "pt-BR";
    public const string English = "en";

    public static IReadOnlyList<string> All { get; } = [PortugueseBrazil, English];
}

/// <summary>Supported UI themes.</summary>
public static class Themes
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    public static IReadOnlyList<string> All { get; } = [Light, Dark, System];
}
