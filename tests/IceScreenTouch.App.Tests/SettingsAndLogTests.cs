// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.App.Tests;

/// <summary>A fresh folder under %TEMP%, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "icescreentouch-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void AMissingFileGivesTheDefaults()
    {
        var store = new SettingsStore(_dir.File("settings.json"));

        var s = store.Load(out var problem);

        Assert.Null(problem);
        Assert.Equal(new AppSettings(), s);
        Assert.Equal(Rotation.R90, s.Rotation);
        Assert.Equal(0.05, s.Wheel.StepPerNotch, 6);
        Assert.Equal(0.10, s.Wheel.Momentum, 6);
        Assert.Equal(WheelOptions.Default, s.Wheel); // the app and the core agree on the defaults
        Assert.Equal(TimeSpan.FromMilliseconds(600), s.Timing.LongPressHold);
    }

    [Fact]
    public void SettingsSurviveARoundTrip()
    {
        var store = new SettingsStore(_dir.File("sub/settings.json"));
        var s = new AppSettings
        {
            AirPlayName = "Desk",
            Receiver = ReceiverMode.AttachToProcess,
            UxPlayPath = @"C:\uxplay\uxplay.exe",
            AttachProcessName = "AirServer",
            DetectPicture = false,
            Screen = "ipad",
            LandscapeRotation = 270,
            WheelStepPercent = 15,
            LongPressMilliseconds = 800,
            CloseToTray = false,
            Window = new WindowPlacement(-1200, 40),
        };

        store.Save(s);
        var loaded = store.Load(out var problem);

        Assert.Null(problem);
        Assert.Equal(s, loaded);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void ALockedFileGivesTheDefaultsAndIsKeptBeforeTheFirstSave()
    {
        var path = _dir.File("settings.json");
        const string Original = """{ "version": 2, "receiver": "AttachToProcess", "attachProcessName": "AirServer" }""";
        File.WriteAllText(path, Original);
        var store = new SettingsStore(path);

        AppSettings s;
        string? problem;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // a scanner holding it
        {
            s = store.Load(out problem);
        }

        Assert.NotNull(problem);
        Assert.Equal(new AppSettings(), s);

        store.Save(s with { AirPlayName = "x" });

        Assert.Equal(Original, File.ReadAllText(store.UnreadPath)); // the real settings are not lost
        Assert.Contains("\"x\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsMissingFromAnOlderFileKeepTheirDefaults()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, """{ "version": 1, "receiver": "AttachToProcess", "attachProcessName": "AirServer" }""");

        var loaded = new SettingsStore(path).Load(out var problem);

        Assert.Null(problem);
        Assert.Equal("AirServer", loaded.AttachProcessName);
        Assert.True(loaded.DetectPicture);
        Assert.True(loaded.WheelScroll);
        Assert.True(loaded.KeyboardInput);
        Assert.Same(ScreenPreset.Auto, loaded.ScreenPreset);
    }

    [Fact]
    public void TheOldDefaultScreenBecomesAutomaticAndAChosenOneStays()
    {
        Assert.Equal("auto", new AppSettings { Version = 1, Screen = "iphone" }.Normalize().Screen);
        Assert.Equal("ipad", new AppSettings { Version = 1, Screen = "ipad" }.Normalize().Screen);
        Assert.Equal("iphone", new AppSettings { Screen = "iphone" }.Normalize().Screen); // chosen in version 2
        Assert.Equal(2, new AppSettings { Version = 1 }.Normalize().Version);
    }

    [Fact]
    public void AutomaticScreenFollowsTheModel()
    {
        var auto = new AppSettings();
        Assert.Equal("ipad", auto.ScreenFor("iPad13,1").Id); // seen: iPad Air 4th generation
        Assert.Equal("iphone", auto.ScreenFor("iPhone16,1").Id); // seen: iPhone 15 Pro
        Assert.Equal("iphone", auto.ScreenFor(null).Id);
        Assert.Equal("ipad", new AppSettings { Screen = "ipad" }.ScreenFor("iPhone16,1").Id); // a chosen shape wins
    }

    [Theory]
    [InlineData("iPhone16,1", "iphone")]
    [InlineData("iPhone10,3", "iphone")] // X
    [InlineData("iPhone10,4", "iphone-home-button")] // 8
    [InlineData("iPhone12,8", "iphone-home-button")] // SE 2nd
    [InlineData("iPhone14,6", "iphone-home-button")] // SE 3rd
    [InlineData("iPhone14,7", "iphone")] // 14
    [InlineData("iPhone8,4", "iphone-home-button")] // SE 1st
    [InlineData("iPhone11,8", "iphone")] // XR
    [InlineData("iPhone17,5", "iphone")] // 16e
    [InlineData("iPhone18,4", "iphone")] // Air
    [InlineData("iPhone18,5", "iphone")] // 17e
    [InlineData("iPhone19,2", "iphone")] // 18 Pro
    [InlineData("iPhone19,4", "ipad-pro-11")] // Duo (foldable)
    [InlineData("iPhone99,1", "iphone")] // newer
    [InlineData("iPod9,1", "iphone-home-button")]
    [InlineData("iPad13,1", "ipad")]
    [InlineData("iPad13,18", "ipad")] // 10th gen
    [InlineData("iPad8,1", "ipad-pro-11")]
    [InlineData("iPad16,3", "ipad-pro-11")]
    [InlineData("iPad8,5", "ipad-4x3")] // Pro 12.9 3rd
    [InlineData("iPad13,8", "ipad-4x3")] // Pro 12.9 5th
    [InlineData("iPad14,1", "ipad-mini")]
    [InlineData("iPad12,1", "ipad-4x3")] // 9th gen
    [InlineData("iPad7,5", "ipad-4x3")]
    [InlineData("iPad11,3", "ipad-4x3")] // Air 3rd
    [InlineData("iPad15,8", "ipad")] // A16
    [InlineData("iPad16,1", "ipad-mini")] // mini A17 Pro
    [InlineData("iPad16,5", "ipad-4x3")] // Pro 13 M4
    [InlineData("iPad16,8", "ipad")] // Air 11 M4
    [InlineData("iPad16,11", "ipad-4x3")] // Air 13 M4
    [InlineData("iPad17,2", "ipad-pro-11")] // Pro 11 M5
    [InlineData("iPad17,3", "ipad-4x3")] // Pro 13 M5
    [InlineData("iPad19,1", "ipad-mini")] // mini A20 Pro
    [InlineData("iPad99,1", "ipad")] // newer
    public void ModelNumbersHaveShapes(string model, string preset)
    {
        Assert.Equal(preset, ScreenPreset.ForModel(model)!.Id);
    }

    // Portrait pixels from Apple's technical specifications: the preset's shape is within the picture locator's 3%.
    [Theory]
    [InlineData("iPhone8,4", 640, 1136)] // SE 1st
    [InlineData("iPhone10,2", 1080, 1920)] // 8 Plus
    [InlineData("iPhone13,1", 1080, 2340)] // 12 mini
    [InlineData("iPhone18,4", 1260, 2736)] // Air
    [InlineData("iPhone19,4", 1878, 2670)] // Duo, inner display
    [InlineData("iPhone19,4", 1398, 2034)] // Duo, outer display
    [InlineData("iPad7,3", 1668, 2224)] // Pro 10.5
    [InlineData("iPad12,1", 1620, 2160)] // 9th gen
    [InlineData("iPad16,1", 1488, 2266)] // mini A17 Pro
    [InlineData("iPad19,1", 1512, 2316)] // mini A20 Pro
    [InlineData("iPad16,3", 1668, 2420)] // Pro 11 M4
    [InlineData("iPad17,1", 1668, 2420)] // Pro 11 M5
    [InlineData("iPad16,8", 1640, 2360)] // Air 11 M4
    [InlineData("iPad14,5", 2048, 2732)] // Pro 12.9 6th
    [InlineData("iPad16,10", 2048, 2732)] // Air 13 M4
    [InlineData("iPad17,3", 2064, 2752)] // Pro 13 M5
    public void ModelShapesMatchTheirScreens(string model, int width, int height)
    {
        var aspect = ScreenPreset.ForModel(model)!.PortraitAspect;
        Assert.InRange(aspect / ((double)width / height), 0.97, 1.03);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Watch6,1")]
    [InlineData("iPhone")]
    [InlineData("iPad13")]
    [InlineData("iPhoneX,1")]
    public void OtherModelNumbersHaveNoShape(string? model)
    {
        Assert.Null(ScreenPreset.ForModel(model));
    }

    [Fact]
    public void TheKeyboardSettingIsSavedAndTheOldLayoutKeyIsIgnored()
    {
        var path = _dir.File("settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { KeyboardInput = false });
        Assert.DoesNotContain("keyboardLayout", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(store.Load(out _).KeyboardInput);

        // A file from before 2026-11-06 may still name a device layout: it loads, and the key goes on the next save.
        File.WriteAllText(path, """{"version":2,"keyboardInput":false,"keyboardLayout":"Jis"}""");
        var loaded = store.Load(out var problem);
        Assert.Null(problem);
        Assert.False(loaded.KeyboardInput);
        store.Save(loaded);
        Assert.DoesNotContain("keyboardLayout", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownScreenIsTheDefaultAndPresetsAreDistinct()
    {
        var normalized = new AppSettings { Screen = "galaxy" }.Normalize();

        Assert.Equal(ScreenPreset.Auto.Id, normalized.Screen);
        Assert.Equal("ipad", new AppSettings { Screen = "IPAD" }.Normalize().Screen);
        Assert.Equal(new SizeD(820, 1180), new AppSettings { Screen = "ipad" }.ScreenPreset.Size);
        Assert.Equal(ScreenPreset.All.Count, ScreenPreset.All.Select(p => p.Id).Distinct().Count());
        Assert.All(ScreenPreset.All, p => Assert.True(p.Width < p.Height, $"{p.Id} is not portrait"));
    }

    [Fact]
    public void TheFileIsReadableJsonWithNamedEnums()
    {
        var store = new SettingsStore(_dir.File("settings.json"));
        store.Save(new AppSettings { Receiver = ReceiverMode.AttachToProcess });

        var json = File.ReadAllText(store.Path);

        Assert.Contains("\"receiver\": \"AttachToProcess\"", json, StringComparison.Ordinal);
        Assert.Contains("\"landscapeRotation\": 90", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"rotation\"", json, StringComparison.Ordinal); // computed properties are not saved
    }

    [Fact]
    public void ABrokenFileIsKeptAsideAndTheDefaultsAreUsed()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, "{ \"airPlayName\": ");
        var store = new SettingsStore(path);

        var s = store.Load(out var problem);

        Assert.Equal(new AppSettings(), s);
        Assert.NotNull(problem);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void HandEditedValuesAreBroughtIntoRange()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, """
            {
              // comments and trailing commas are fine
              "airPlayName": "  ",
              "landscapeRotation": 45,
              "wheelStepPercent": 500,
              "longPressMilliseconds": 10,
              "uxPlayPath": "",
              "unknownSetting": true,
            }
            """);

        var s = new SettingsStore(path).Load(out var problem);

        Assert.Null(problem);
        Assert.Equal("IceScreen Touch", s.AirPlayName);
        Assert.Equal(90, s.LandscapeRotation);
        Assert.Equal(AppSettings.MaxWheelStepPercent, s.WheelStepPercent);
        Assert.Equal(AppSettings.MinLongPressMilliseconds, s.LongPressMilliseconds);
        Assert.Null(s.UxPlayPath);
    }

    [Theory]
    [InlineData("iScrMirror", "IceScreen Touch")]   // the former default follows the rename
    [InlineData(" iScrMirror ", "IceScreen Touch")]
    [InlineData("Living room", "Living room")]      // a name the user chose stays
    public void TheFormerDefaultAirPlayNameReadsAsTheNewDefault(string stored, string expected)
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, $$"""{ "airPlayName": "{{stored}}" }""");

        Assert.Equal(expected, new SettingsStore(path).Load(out _).AirPlayName);
    }
}

public sealed class FormerSettingsImportTests : IDisposable
{
    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Root => _dir.File("IceScreenTouch");

    private string Former => _dir.File("iScrMirror");

    [Fact]
    public void TheFirstStartCopiesTheFormerSettings()
    {
        Directory.CreateDirectory(Former);
        File.WriteAllText(Path.Combine(Former, "settings.json"), "{\"wheelStepPercent\": 7}");

        var copied = AppPaths.ImportFormerSettings(Root, Former);

        Assert.Equal(Path.Combine(Former, "settings.json"), copied);
        Assert.Equal("{\"wheelStepPercent\": 7}", File.ReadAllText(Path.Combine(Root, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Former, "settings.json"))); // the former folder is left alone
    }

    [Fact]
    public void ExistingSettingsAreNotOverwritten()
    {
        Directory.CreateDirectory(Former);
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Former, "settings.json"), "old");
        File.WriteAllText(Path.Combine(Root, "settings.json"), "new");

        Assert.Null(AppPaths.ImportFormerSettings(Root, Former));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Root, "settings.json")));
    }

    [Fact]
    public void NothingToImportLeavesTheFolderUncreated()
    {
        Assert.Null(AppPaths.ImportFormerSettings(Root, Former));
        Assert.Null(AppPaths.ImportFormerSettings(Root, null));
        Assert.False(Directory.Exists(Root));
    }
}

public sealed class AppLogTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 21, 30, 0, TimeSpan.Zero));

    public AppLogTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void LinesGoToTheFileAndToTheRecentList()
    {
        string? added = null;
        using (var log = new AppLog(_dir.Path, _time))
        {
            log.LineAdded += (_, l) => added = l;
            log.Write("hello");

            Assert.Equal("21:30:00.000 hello", added);
            Assert.Equal(["21:30:00.000 hello"], log.Recent());
            Assert.Equal(_dir.File("IceScreenTouch-20261105-213000.log"), log.FilePath);
        }

        Assert.Equal(["2026-11-05 21:30:00.000 hello"], File.ReadAllLines(_dir.File("IceScreenTouch-20261105-213000.log")));
    }

    [Fact]
    public void TheRecentListIsBounded()
    {
        using var log = new AppLog(null, _time);
        for (var i = 0; i < AppLog.RecentLines + 20; i++)
        {
            log.Write(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var recent = log.Recent();
        Assert.Equal(AppLog.RecentLines, recent.Count);
        Assert.EndsWith(" 20", recent[0], StringComparison.Ordinal);
        Assert.Null(log.FilePath);
        Assert.Null(log.NewLogFile("uxplay"));
    }

    [Fact]
    public void OldLogFilesArePruned()
    {
        for (var day = 1; day <= 12; day++)
        {
            File.WriteAllText(_dir.File($"IceScreenTouch-202611{day:00}-000000.log"), "old");
            File.WriteAllText(_dir.File($"uxplay-202611{day:00}-000000.log"), "old");
        }

        _time.SetUtcNow(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero));
        using var log = new AppLog(_dir.Path, _time);
        var uxplay = log.NewLogFile("uxplay");
        File.WriteAllText(uxplay!, "new");

        var app = Directory.GetFiles(_dir.Path, "IceScreenTouch-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var ux = Directory.GetFiles(_dir.Path, "uxplay-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(AppLog.KeepFiles, app.Count);
        Assert.Equal("IceScreenTouch-20261104-000000.log", app[0]); // the 3 oldest are gone
        Assert.Equal("IceScreenTouch-20261120-000000.log", app[^1]);
        Assert.Equal(AppLog.KeepFiles, ux.Count);
        Assert.Equal("uxplay-20261120-000000.log", ux[^1]);
    }

    [Fact]
    public void TwoFilesInTheSameSecondGetDistinctNames()
    {
        using var log = new AppLog(_dir.Path, _time);
        var a = log.NewLogFile("uxplay");
        File.WriteAllText(a!, "a");
        var b = log.NewLogFile("uxplay");

        Assert.NotEqual(a, b);
        Assert.EndsWith("-2.log", b, StringComparison.Ordinal);
    }
}
