// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;

namespace IceScreenTouch.App.Tests;

/// <summary>The automation API's settings: defaults, the file, ranges, and that they never restart the session.</summary>
public sealed class ApiSettingsTests : IDisposable
{
    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TheApiIsOffOnTheDefaultPort()
    {
        var settings = new AppSettings();

        Assert.False(settings.ApiEnabled);
        Assert.Equal(47821, settings.ApiPort);
        Assert.Equal(ApiServer.DefaultPort, settings.ApiPort);
    }

    [Fact]
    public void TheSwitchAndThePortAreSavedUnderTheirOwnNamesAndNeverWithAToken()
    {
        var store = new SettingsStore(_dir.File("settings.json"));
        store.Save(new AppSettings { ApiEnabled = true, ApiPort = 50123 });

        var json = File.ReadAllText(store.Path);
        Assert.Contains("\"apiEnabled\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"apiPort\": 50123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);

        var loaded = store.Load(out var problem);
        Assert.Null(problem);
        Assert.True(loaded.ApiEnabled);
        Assert.Equal(50123, loaded.ApiPort);
    }

    [Fact]
    public void AnOlderFileWithoutTheApiFieldsGetsTheDefaults()
    {
        File.WriteAllText(_dir.File("settings.json"), """{ "version": 2, "airPlayName": "Desk" }""");

        var loaded = new SettingsStore(_dir.File("settings.json")).Load(out var problem);

        Assert.Null(problem);
        Assert.Equal("Desk", loaded.AirPlayName);
        Assert.False(loaded.ApiEnabled);
        Assert.Equal(47821, loaded.ApiPort);
    }

    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(65535, 65535)]
    [InlineData(47821, 47821)]
    [InlineData(1023, 47821)]
    [InlineData(80, 47821)]
    [InlineData(8000, 47821)] // the probe page server's port
    [InlineData(0, 47821)]
    [InlineData(-5, 47821)]
    [InlineData(65536, 47821)]
    public void AnOutOfRangePortBecomesTheDefault(int port, int expected)
    {
        Assert.Equal(expected, (new AppSettings { ApiPort = port }).Normalize().ApiPort);
    }

    [Fact]
    public void ChangingTheApiNeverNeedsASessionRestart()
    {
        var before = new AppSettings();

        Assert.False(AppSettings.NeedsRestart(before, before with { ApiEnabled = true }));
        Assert.False(AppSettings.NeedsRestart(before, before with { ApiPort = 50000 }));
        Assert.True(AppSettings.NeedsRestart(before, before with { AirPlayName = "Other" }));
    }

    [Theory]
    [InlineData(LicenseKind.Purchased, true)]
    [InlineData(LicenseKind.NotPackaged, true)]
    [InlineData(LicenseKind.Unknown, false)]
    [InlineData(LicenseKind.Trial, true)]
    [InlineData(LicenseKind.Expired, false)]
    public void TheApiBelongsToThePurchasedAppABuildOfYourOwnAndTheRunningTrial(LicenseKind kind, bool allowed)
    {
        var license = new LicenseState(kind, kind == LicenseKind.Trial ? DateTimeOffset.UnixEpoch.AddYears(60) : null);
        Assert.Equal(allowed, ApiService.Allows(license));
        Assert.Equal(kind == LicenseKind.Trial, ApiService.IsTrial(license)); // only the trial has the quota
    }
}

/// <summary>The token file: created once, kept, regenerated on request, replaced when corrupt, never logged.</summary>
public sealed class ApiTokenStoreTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly List<string> _log = [];

    public void Dispose() => _dir.Dispose();

    private ApiTokenStore Store(Action<string>? secure = null) => new(_dir.Path, _log.Add, secure);

    [Fact]
    public void ItCreatesAValidTokenTheFirstTime()
    {
        var store = Store();
        Assert.False(File.Exists(store.Path));

        var token = store.GetOrCreate();

        Assert.True(ApiToken.IsValid(token));
        Assert.Equal(token, File.ReadAllText(store.Path));
        Assert.Equal(Path.Combine(_dir.Path, "api-token"), store.Path);
        Assert.Empty(_log);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public void ItKeepsTheTokenUntilTheUserRegeneratesIt()
    {
        var first = Store().GetOrCreate();

        Assert.Equal(first, Store().GetOrCreate()); // another run of the app
        Assert.Equal(first, Store().GetOrCreate());

        var second = Store().Regenerate();

        Assert.NotEqual(first, second);
        Assert.True(ApiToken.IsValid(second));
        Assert.Equal(second, Store().GetOrCreate());
    }

    [Fact]
    public void AFinalNewlineDoesNotMakeTheTokenInvalid()
    {
        var token = Store().GetOrCreate();
        File.WriteAllText(Path.Combine(_dir.Path, "api-token"), token + "\r\n"); // rewriting keeps the file's private ACL

        Assert.Equal(token, Store().GetOrCreate());
        Assert.Empty(_log);
    }

    [Fact]
    public void AFileThatCannotBeReadRightNowIsNeverReplacedAndTheStartFailsInstead()
    {
        var token = Store().GetOrCreate();
        var path = Path.Combine(_dir.Path, "api-token");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Another program holds the file (a scanner, a backup): reading it fails after a few tries; the token must not be
            // thrown away for that.
            Assert.Throws<IOException>(() => Store().GetOrCreate());
        }

        Assert.Empty(_log);
        Assert.Equal(token, File.ReadAllText(path));
        Assert.Equal(token, Store().GetOrCreate());
    }

    [Fact]
    public void AFileThatOtherAccountsCouldHaveWrittenOrReadIsReplaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the Unix mode check is the same call as for api.json
        }

        var old = Store().GetOrCreate();
        var path = Path.Combine(_dir.Path, "api-token");
        File.Delete(path);
        File.WriteAllText(path, old); // a valid token, but in a file with the folder's inherited ACL (others may be on it)

        var token = Store().GetOrCreate();

        Assert.NotEqual(old, token);
        var line = Assert.Single(_log);
        Assert.Contains("not private", line, StringComparison.Ordinal);
        Assert.DoesNotContain(old, line, StringComparison.Ordinal);
        Assert.True(ApiConnectionFile.IsProtectedToCurrentUser(path));
        Assert.Equal(token, Store().GetOrCreate()); // and the replacement is trusted from then on
    }

    [Fact]
    public void OnWindowsTheTokenFileCarriesTheMediumLabelAndAnOlderFileGetsIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var token = Store().GetOrCreate();
        var path = Path.Combine(_dir.Path, "api-token");
        var label = ApiConnectionFile.GetIntegrityLabel(path);
        Assert.Matches(@"^S:[A-Z]*\(ML;;(NW|NR){2};;;ME\)$", label); // Medium, No-Write-Up and No-Read-Up (flags in front are the system's)

        // A file of an earlier version: protected, but without the label. It keeps its token and is labelled.
        File.Delete(path);
        using (var stream = ApiConnectionFile.CreateProtected(path))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(token));
        }

        Assert.Equal(token, Store().GetOrCreate());
        Assert.Matches(@"^S:[A-Z]*\(ML;;(NW|NR){2};;;ME\)$", ApiConnectionFile.GetIntegrityLabel(path));
    }

    [Fact]
    public void ALeftoverTemporaryFileOfACutShortWriteIsCleanedUp()
    {
        var leftover = Path.Combine(_dir.Path, "api-token.0123456789abcdef.tmp");
        File.WriteAllText(leftover, "partial");

        _ = Store().GetOrCreate();

        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public void TheFileIsCreatedProtectedInOneStep()
    {
        var path = Path.Combine(_dir.Path, "probe.bin");

        using (var stream = ApiConnectionFile.CreateProtected(path))
        {
            // Protected while it is still open and empty: there is no unprotected moment.
            Assert.True(ApiConnectionFile.IsProtectedToCurrentUser(path));
            stream.WriteByte(1);
        }

        Assert.Throws<IOException>(() => ApiConnectionFile.CreateProtected(path)); // CreateNew: never opens an existing file
    }

    [Fact]
    public void AProtectedFileIsOwnedByTheCurrentUserEvenWhenTheProcessIsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir.Path, "owner.bin");
        using (ApiConnectionFile.CreateProtected(path))
        {
        }

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        Assert.Equal(user, owner); // an elevated token would otherwise make it BUILTIN\Administrators
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("garbage-xyz")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ACorruptFileIsReplacedAndTheLogSaysSoWithoutTheContent(string content)
    {
        File.WriteAllText(Path.Combine(_dir.Path, "api-token"), content);

        var token = Store().GetOrCreate();

        Assert.True(ApiToken.IsValid(token));
        Assert.Equal(token, File.ReadAllText(Path.Combine(_dir.Path, "api-token")));
        var line = Assert.Single(_log);
        Assert.StartsWith("[api]", line, StringComparison.Ordinal);
        Assert.DoesNotContain(token, line, StringComparison.Ordinal);
        if (content.Trim().Length > 0)
        {
            Assert.DoesNotContain(content.Trim(), line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFailedRestrictionPublishesNothingAndLeavesNoTemporaryFile()
    {
        var store = Store(_ => throw new UnauthorizedAccessException("no ACL"));

        Assert.Throws<UnauthorizedAccessException>(store.GetOrCreate);

        Assert.False(File.Exists(store.Path));
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void TheFileIsRestrictedBeforeTheTokenIsWritten()
    {
        string? seenAtRestriction = null;
        var store = Store(temp => seenAtRestriction = File.ReadAllText(temp));

        _ = store.GetOrCreate();

        Assert.Equal(string.Empty, seenAtRestriction); // the temporary file was still empty when it was restricted
    }

    [Fact]
    public void WithTheDefaultRestrictionOnlyTheCurrentUserCanReadTheFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the Unix mode is covered by the API tests of api.json
        }

        var store = Store();
        _ = store.GetOrCreate();

        var rules = new FileInfo(store.Path).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
        {
            Assert.Equal(me, rule.IdentityReference);
        }
    }
}
