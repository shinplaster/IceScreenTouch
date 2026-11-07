using Avalonia.Styling;
using IceScreenTouch.App.Resources;

namespace IceScreenTouch.App.Settings;

/// <summary>A light/dark choice of the settings form.</summary>
/// <param name="Code">Stored in settings.json; null = the Windows app mode.</param>
/// <param name="Name">Shown in the settings form.</param>
public sealed record ThemeChoice(string? Code, string Name)
{
    /// <summary>The combo box shows this.</summary>
    public override string ToString() => Name;
}

/// <summary>Light or dark. The OS app mode by default; the settings can name one. Applied at once (no restart).</summary>
public static class AppTheme
{
    /// <summary>The stored codes.</summary>
    public const string Light = "light";

    /// <inheritdoc cref="Light"/>
    public const string Dark = "dark";

    /// <summary>The choices of the settings form, "automatic" first.</summary>
    public static IReadOnlyList<ThemeChoice> Choices =>
    [
        new(null, Strings.ThemeAuto),
        new(Light, Strings.ThemeLight),
        new(Dark, Strings.ThemeDark),
    ];

    /// <summary>"light", "dark", or null (automatic) for anything else.</summary>
    public static string? Normalize(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "LIGHT" => Light,
        "DARK" => Dark,
        _ => null,
    };

    /// <summary>The Avalonia theme variant for <paramref name="code"/> (Default follows the OS).</summary>
    public static ThemeVariant ToVariant(string? code) => Normalize(code) switch
    {
        Light => ThemeVariant.Light,
        Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
