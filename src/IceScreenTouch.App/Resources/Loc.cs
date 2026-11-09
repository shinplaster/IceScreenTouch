// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace IceScreenTouch.App.Resources;

/// <summary>Formatting of the localized text in <see cref="Strings"/>.</summary>
public static class Loc
{
    /// <summary>Fills a "Format..." string with the user's number format.</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);
}

/// <summary>A language the user can pick in the settings.</summary>
/// <param name="Code">Stored in settings.json; null = the OS display language.</param>
/// <param name="Name">Shown in the settings form (each language in its own words).</param>
public sealed record LanguageChoice(string? Code, string Name)
{
    /// <summary>The combo box shows this.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The language of the user interface. The OS display language by default; the settings can name one. Applied
/// once at start-up, before the first window: the views read their text when they are created.
/// </summary>
public static class AppLanguage
{
    /// <summary>The languages the app has text for (English is the neutral resources).</summary>
    public static IReadOnlyList<string> Supported { get; } = ["en", "ja"];

    /// <summary>The choices of the settings form, "automatic" first.</summary>
    public static IReadOnlyList<LanguageChoice> Choices =>
    [
        new(null, Strings.LanguageAuto),
        new("en", "English"),
        new("ja", "日本語"),
    ];

    /// <summary>A supported code, or null (automatic) for anything else.</summary>
    public static string? Normalize(string? code)
    {
        var trimmed = code?.Trim();
        return Supported.FirstOrDefault(s => string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Uses <paramref name="code"/> for the text of every thread (null = keep the OS display language). Number
    /// and date formats stay those of the OS.
    /// </summary>
    public static void Apply(string? code)
    {
        if (Normalize(code) is not { } supported)
        {
            return;
        }

        var culture = CultureInfo.GetCultureInfo(supported);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
