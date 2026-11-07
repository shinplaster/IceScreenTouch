using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.App.ViewModels;

namespace IceScreenTouch.App.Tests;

/// <summary>
/// The tests that compare text expect Japanese, whatever the language of the machine that runs them; a test that
/// wants another language uses <see cref="UiCulture"/>.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseJapanese()
    {
        var ja = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentUICulture = ja;
        CultureInfo.CurrentUICulture = ja;
    }
}

/// <summary>Switches the UI language of the current test (the culture flows with the test's async context).</summary>
internal sealed class UiCulture : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    public UiCulture(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}

public sealed partial class LocalizationTests
{
    private static readonly MirrorStatus Ready = new()
    {
        Running = true,
        Ble = BleState.Connected,
        Receiver = ReceiverState.Showing,
        ReceiverName = "UxPlay",
    };

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static Dictionary<string, string> Entries(string culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"no resources for '{culture}'");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    private static int[] Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order()];

    [Fact]
    public void JapaneseHasTheSameNamesAsEnglish()
    {
        var en = Entries(CultureInfo.InvariantCulture.Name);
        var ja = Entries("ja");

        Assert.NotEmpty(en);
        Assert.Empty(en.Keys.Except(ja.Keys));
        Assert.Empty(ja.Keys.Except(en.Keys));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ja")]
    public void EveryTextIsFilledAndFormatsHaveTheirPlaceholders(string culture)
    {
        var en = Entries(CultureInfo.InvariantCulture.Name);
        foreach (var (name, text) in Entries(culture))
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{culture}/{name} is empty");
            var used = Placeholders(text);
            Assert.True(used.SequenceEqual(Placeholders(en[name])), $"{culture}/{name}: placeholders {string.Join(",", used)} differ from English");
            if (name.StartsWith("Format", StringComparison.Ordinal))
            {
                Assert.True(used.Length > 0, $"{name} has no placeholder");
                Assert.True(used.SequenceEqual(Enumerable.Range(0, used.Length)), $"{culture}/{name}: placeholders are not 0..n-1");

                // A composite format that string.Format can fill (no stray braces).
                _ = Loc.Format(text, [.. used.Select(_ => (object?)1.5)]);
            }
            else
            {
                Assert.True(used.Length == 0, $"{culture}/{name} has placeholders but is not a Format... text");
            }
        }
    }

    [Fact]
    public void JapaneseIsUsedForJapaneseAndEnglishForTheRest()
    {
        using (new UiCulture("ja-JP"))
        {
            Assert.Equal("状態", Strings.TabStatus);
        }

        using (new UiCulture("en-US"))
        {
            Assert.Equal("Status", Strings.TabStatus);
        }

        using (new UiCulture("de-DE"))
        {
            Assert.Equal("Status", Strings.TabStatus);
        }
    }

    [Fact]
    public void ViewModelsSpeakEnglish()
    {
        using var _ = new UiCulture("en-US");

        Assert.Equal("Connected to the iPhone", MainViewModel.DescribeBle(Ready).Text);
        Assert.Equal("UxPlay: showing", MainViewModel.DescribeReceiver(Ready).Text);
        Assert.Equal("Ready", MainViewModel.DescribeHeadline(Ready).Text);
        Assert.Contains("Accessibility > Zoom", MainViewModel.DescribeNextStep(Ready, new AppSettings()), StringComparison.Ordinal);
        Assert.Contains(
            "Control Center > Screen Mirroring and choose \"IceScreen Touch\"",
            MainViewModel.DescribeNextStep(Ready with { Receiver = ReceiverState.Waiting }, new AppSettings()),
            StringComparison.Ordinal);

        var steps = SetupViewModel.BuildSteps(Ready, new AppSettings(), touchVerified: false, "MY-PC");
        Assert.Equal(["Bluetooth adapter", "Connect the iPhone (Bluetooth)", "Zoom on the iPhone", "AirPlay receiver", "Screen Mirroring"], steps.Select(s => s.Title));

        Assert.StartsWith("Another test page is open", ProbeViewModel.DescribeWaiting(ProbeKind.Scroll, ProbeWaitingFor.WrongPage, FirewallState.Allowed), StringComparison.Ordinal);
        Assert.Equal("iPad mini (6th generation and later)", ScreenPreset.Find("ipad-mini")!.Label);
    }

    [Fact]
    public void ScreenPresetsHaveTextInEveryLanguage()
    {
        foreach (var culture in (string[])["en-US", "ja-JP"])
        {
            using var _ = new UiCulture(culture);
            foreach (var preset in ScreenPreset.All)
            {
                Assert.NotEqual(preset.Id, preset.Label);
                Assert.Equal(preset.Label, preset.ToString());
            }
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("ja", "ja")]
    [InlineData(" JA ", "ja")]
    [InlineData("en", "en")]
    [InlineData("fr", null)]
    public void LanguageIsNormalized(string? stored, string? expected)
    {
        Assert.Equal(expected, AppLanguage.Normalize(stored));
        Assert.Equal(expected, new AppSettings { Language = stored }.Normalize().Language);
    }

    [Fact]
    public void LanguageChoicesStartWithAutomatic()
    {
        Assert.Equal([null, "en", "ja"], AppLanguage.Choices.Select(c => c.Code));
        Assert.All(AppLanguage.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }
}
