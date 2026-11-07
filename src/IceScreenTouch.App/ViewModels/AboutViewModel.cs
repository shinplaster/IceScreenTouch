using System.Globalization;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.ViewModels;

/// <summary>
/// The About tab: version and commit, copyright, links, the license and the third-party components, the data
/// folder, the versions kept apart from the app's (HID map, settings file), the trademark notice. Opening links and
/// folders and the clipboard are the view's (they need the window).
/// </summary>
public sealed class AboutViewModel : ObservableObject
{
    private bool _copied;
    private string _licenseStatusText = LicenseLines.About(LicenseState.NotPackaged, TimeZoneInfo.Local, CultureInfo.CurrentUICulture);

    /// <summary>The product name.</summary>
    public string ProductName { get; } = AppInfo.ProductName;

    /// <summary>"Version 0.1.0 (commit e51464e)".</summary>
    public string VersionText { get; } = AppInfo.Commit is { } commit
        ? Loc.Format(Strings.FormatAboutVersionCommit, AppInfo.Version, commit)
        : Loc.Format(Strings.FormatAboutVersion, AppInfo.Version);

    /// <summary>The copyright line.</summary>
    public string Copyright { get; } = AppInfo.Copyright;

    /// <summary>The MIT license text.</summary>
    public string LicenseText { get; } = AppInfo.LicenseText;

    /// <summary>The bundled components, one per line ("name (package version): license - url").</summary>
    public IReadOnlyList<string> Components { get; } = AppInfo.Components;

    /// <summary>"16 components": the header of the folded list.</summary>
    public string ComponentsHeader => Loc.Format(Strings.FormatComponents, Components.Count);

    /// <summary>The data folder (settings and logs), as a program outside the app sees it (see <see cref="AppPaths.ForExternalProgram"/>).</summary>
    public string DataFolder { get; } = AppPaths.ForExternalProgram(AppPaths.Root);

    /// <summary>The HID report map's version, and that a new one means pairing again.</summary>
    public string HidMapText { get; } = Loc.Format(Strings.FormatHidMapVersion, HidReports.ReportMapVersion, HidReports.KeyboardReportMap.Length);

    /// <summary>The settings file's format version.</summary>
    public string SettingsVersionText { get; } = Loc.Format(Strings.FormatSettingsVersion, AppSettings.CurrentVersion);

    /// <summary>"License: Purchased" and the like: the Store license of this copy (not the MIT license text).</summary>
    public string LicenseStatusText { get => _licenseStatusText; private set => Set(ref _licenseStatusText, value); }

    /// <summary>Shows the current Store license.</summary>
    public void ShowLicense(LicenseState state, TimeZoneInfo zone) =>
        LicenseStatusText = LicenseLines.About(state, zone, CultureInfo.CurrentUICulture);

    /// <summary>What "copy version info" puts on the clipboard (English, for a problem report).</summary>
    public string Diagnostics { get; } = AppInfo.Diagnostics;

    /// <summary>The version info was just copied (the view shows a short note).</summary>
    public bool Copied { get => _copied; set => Set(ref _copied, value); }
}
