using System.Text.Json.Serialization;
using IceScreenTouch.App.Resources;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Settings;

/// <summary>Where the picture comes from.</summary>
public enum ReceiverMode
{
    /// <summary>The app starts uxplay.exe and stops it on exit.</summary>
    StartUxPlay,

    /// <summary>Follow the window of a receiver that is already running (started by hand).</summary>
    AttachToProcess,
}

/// <summary>Saved window position (screen pixels).</summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
public sealed record WindowPlacement(int X, int Y);

/// <summary>Everything the app remembers between runs (settings.json).</summary>
public sealed record AppSettings
{
    /// <summary>The format version this build writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Format version of the file.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>AirPlay name shown on the iPhone.</summary>
    public string AirPlayName { get; init; } = "IceScreen Touch";

    /// <summary>The default AirPlay name under the app's former name; read as the current default.</summary>
    public const string FormerDefaultAirPlayName = "iScrMirror";

    /// <summary>Start uxplay or follow a running receiver.</summary>
    public ReceiverMode Receiver { get; init; } = ReceiverMode.StartUxPlay;

    /// <summary>uxplay.exe; null = look for the local build (poc/poc1-uxplay/UxPlay/build-isr, then build).</summary>
    public string? UxPlayPath { get; init; }

    /// <summary>MSYS2 ucrt64\bin put first on PATH for uxplay; null = the scoop install.</summary>
    public string? MsysBin { get; init; }

    /// <summary>GStreamer video sink for uxplay.</summary>
    public string VideoSink { get; init; } = "d3d11videosink";

    /// <summary>Process name to follow in <see cref="ReceiverMode.AttachToProcess"/> (without .exe).</summary>
    public string AttachProcessName { get; init; } = "uxplay";

    /// <summary>
    /// In <see cref="ReceiverMode.AttachToProcess"/>, find the picture by looking at the window (receivers that
    /// letterbox it, such as AirServer full screen). Off = a centered portrait picture is assumed.
    /// </summary>
    public bool DetectPicture { get; init; } = true;

    /// <summary>
    /// The device's screen shape (<see cref="ScreenPreset.Id"/>): where to look for the picture when the receiver
    /// does not report its size. "auto" (the default since version 2) takes the shape of the device that gets the
    /// input from its model number; files of version 1 had "iphone" as the default, which becomes "auto".
    /// </summary>
    public string Screen { get; init; } = ScreenPreset.Auto.Id;

    /// <summary>Rotation for a landscape picture (90 or 270; Ctrl+R and the rotate button switch it).</summary>
    public int LandscapeRotation { get; init; } = 90;

    /// <summary>
    /// Resize the mirror window when the picture size changes (UxPlay started by the app), or reshape a running
    /// receiver's window when the picture rotates inside it (needs <see cref="DetectPicture"/>).
    /// </summary>
    public bool FollowVideoSize { get; init; } = true;

    /// <summary>The right button is a long press.</summary>
    public bool RightButtonLongPress { get; init; } = true;

    /// <summary>Minimum hold of a long press in milliseconds.</summary>
    public int LongPressMilliseconds { get; init; } = 600;

    /// <summary>The wheel scrolls.</summary>
    public bool WheelScroll { get; init; } = true;

    /// <summary>Finger travel per wheel notch, in percent of the picture height.</summary>
    public int WheelStepPercent { get; init; } = 5;

    /// <summary>Momentum when the wheel stops, in percent (0 = the content stops with the wheel).</summary>
    public int WheelMomentumPercent { get; init; } = 10;

    /// <summary>Keys typed while the mirror window has the focus go to the device (the HID keyboard).</summary>
    public bool KeyboardInput { get; init; } = true;

    /// <summary>Log every touch.</summary>
    public bool VerboseLog { get; init; }

    /// <summary>Closing the window keeps the app in the notification area.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Do not use Bluetooth; record the touches instead (for testing).</summary>
    public bool DryRun { get; init; }

    /// <summary>Language of the user interface ("en", "ja"); null = the OS display language.</summary>
    public string? Language { get; init; }

    /// <summary>Light or dark ("light", "dark"); null = the Windows app mode.</summary>
    public string? Theme { get; init; }

    /// <summary>Where the status window was last.</summary>
    public WindowPlacement? Window { get; init; }

    /// <summary>
    /// The device chosen on the status tab to get touches and keys (its Bluetooth device ID); null = automatic: the
    /// device the mirror window shows, else the one that connected last. Changed while running, without a restart.
    /// </summary>
    public string? TargetDevice { get; init; }

    /// <summary>The Bluetooth name of <see cref="TargetDevice"/>, to show it while it is not connected.</summary>
    public string? TargetDeviceName { get; init; }

    /// <summary>The landscape rotation as the core type.</summary>
    [JsonIgnore]
    public Rotation Rotation => (Rotation)LandscapeRotation;

    /// <summary>The screen preset (automatic for an unknown id).</summary>
    [JsonIgnore]
    public ScreenPreset ScreenPreset => ScreenPreset.Find(Screen) ?? ScreenPreset.Auto;

    /// <summary>
    /// The shape to use for a device with <paramref name="model"/> (null = not known yet): the chosen preset, or
    /// with <see cref="ScreenPreset.Auto"/> the model's shape (<see cref="ScreenPreset.Default"/> when unknown).
    /// </summary>
    public ScreenPreset ScreenFor(string? model) =>
        ScreenPreset.IsAuto ? ScreenPreset.ForModel(model) ?? ScreenPreset.Default : ScreenPreset;

    /// <summary>The wheel settings as the core type.</summary>
    [JsonIgnore]
    public WheelOptions Wheel => WheelOptions.Default with { StepPerNotch = WheelStepPercent / 100.0, Momentum = WheelMomentumPercent / 100.0 };

    /// <summary>The touch timing as the core type.</summary>
    [JsonIgnore]
    public TouchTiming Timing => TouchTiming.Default with { LongPressHold = TimeSpan.FromMilliseconds(LongPressMilliseconds) };

    /// <summary>Limits of the numeric settings.</summary>
    public const int MinWheelStepPercent = 2;

    /// <inheritdoc cref="MinWheelStepPercent"/>
    public const int MaxWheelStepPercent = 50;

    /// <inheritdoc cref="MinWheelStepPercent"/>
    public const int MaxWheelMomentumPercent = 100;

    /// <inheritdoc cref="MinWheelStepPercent"/>
    public const int MinLongPressMilliseconds = 300;

    /// <inheritdoc cref="MinWheelStepPercent"/>
    public const int MaxLongPressMilliseconds = 3000;

    /// <summary>A copy with every value in range (hand-edited or older files).</summary>
    public AppSettings Normalize()
    {
        var defaults = new AppSettings();
        return this with
        {
            Version = defaults.Version,
            AirPlayName = string.IsNullOrWhiteSpace(AirPlayName) || AirPlayName.Trim() == FormerDefaultAirPlayName ? defaults.AirPlayName : AirPlayName.Trim(),
            Receiver = Enum.IsDefined(Receiver) ? Receiver : defaults.Receiver,
            UxPlayPath = string.IsNullOrWhiteSpace(UxPlayPath) ? null : UxPlayPath.Trim(),
            MsysBin = string.IsNullOrWhiteSpace(MsysBin) ? null : MsysBin.Trim(),
            VideoSink = string.IsNullOrWhiteSpace(VideoSink) ? defaults.VideoSink : VideoSink.Trim(),
            AttachProcessName = string.IsNullOrWhiteSpace(AttachProcessName) ? defaults.AttachProcessName : AttachProcessName.Trim(),
            Screen = Version < 2 && ScreenPreset.Find(Screen) == ScreenPreset.Default ? defaults.Screen : ScreenPreset.Find(Screen)?.Id ?? defaults.Screen,
            LandscapeRotation = LandscapeRotation is 0 or 90 or 180 or 270 ? LandscapeRotation : defaults.LandscapeRotation,
            LongPressMilliseconds = Math.Clamp(LongPressMilliseconds, MinLongPressMilliseconds, MaxLongPressMilliseconds),
            WheelStepPercent = Math.Clamp(WheelStepPercent, MinWheelStepPercent, MaxWheelStepPercent),
            WheelMomentumPercent = Math.Clamp(WheelMomentumPercent, 0, MaxWheelMomentumPercent),
            Language = AppLanguage.Normalize(Language),
            Theme = AppTheme.Normalize(Theme),
            TargetDevice = string.IsNullOrWhiteSpace(TargetDevice) ? null : TargetDevice.Trim(),
            TargetDeviceName = string.IsNullOrWhiteSpace(TargetDevice) || string.IsNullOrWhiteSpace(TargetDeviceName) ? null : TargetDeviceName.Trim(),
        };
    }

    /// <summary>
    /// Whether going from <paramref name="before"/> to <paramref name="after"/> needs the session restarted. The
    /// looks and the window (theme, language, close to tray, position) do not, nor does the target device (the
    /// running session takes it as it is).
    /// </summary>
    public static bool NeedsRestart(AppSettings before, AppSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return SessionPart(before) != SessionPart(after);

        static AppSettings SessionPart(AppSettings s) =>
            s.Normalize() with { Theme = null, Language = null, CloseToTray = false, Window = null, TargetDevice = null, TargetDeviceName = null };
    }
}
