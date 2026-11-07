using System.Globalization;
using IceScreenTouch.Core;

namespace IceScreenTouch.Bridge;

/// <summary>Command-line settings (same options as poc/poc3-integrated/mirror_touch.py where they exist).</summary>
internal sealed record BridgeSettings
{
    public string Name { get; init; } = "IceScreen Touch";

    public string Sink { get; init; } = "d3d11videosink";

    public string? UxPlayExe { get; init; }

    public IReadOnlyList<string> UxPlayArgs { get; init; } = [];

    public string? UxPlayLog { get; init; }

    public string? MsysBin { get; init; }

    /// <summary>Attach to an already running process with this name instead of starting uxplay.</summary>
    public string? Attach { get; init; }

    public bool Follow { get; init; } = true;

    public Rotation LandscapeRotation { get; init; } = Rotation.R90;

    public bool DryRun { get; init; }

    public bool Verbose { get; init; }

    public double? DurationSeconds { get; init; }

    public bool Collect { get; init; }

    public bool Help { get; init; }
}

/// <summary>Parses the command line.</summary>
internal static class BridgeCommandLine
{
    public const string Usage = """
        usage: IceScreenTouch.Bridge [options]

        Mirror the iPhone with UxPlay and control it by clicking the UxPlay window (port of PoC 3).
          left drag         one-finger touch / swipe
          Ctrl + left drag  two-finger pinch (second finger mirrored through the screen center)
          right button      long press (held at least 600 ms; drag to move)
          wheel             scroll (a short swipe; Shift + wheel or tilt scrolls sideways)
          H                 home gesture (the UxPlay window must have the focus)
          R                 switch the landscape rotation 90 <-> 270

        options:
          --name NAME            AirPlay name shown on the iPhone (default IceScreen Touch)
          --sink SINK            GStreamer video sink for UxPlay (default d3d11videosink)
          --uxplay-exe PATH      uxplay.exe (default: poc/poc1-uxplay/UxPlay/build-isr, else build)
          --uxplay-arg ARG       extra uxplay argument (repeatable)
          --uxplay-log FILE      write the full uxplay output to this file
          --msys-bin DIR         MSYS2 ucrt64\bin put first on PATH (default: the scoop install)
          --attach [NAME]        do not start uxplay; follow the window of a running process (default uxplay)
          --no-follow            do not resize the window when the picture size changes
          --landscape-rot DEG    rotation for a landscape picture: 0, 90, 180, 270 (default 90)
          --dry-run              no Bluetooth; record the touches instead (use -v to see them)
          -v, --verbose          print every touch
          --duration SECONDS     exit after this many seconds (for tests)
          --collect              record a timestamped uxplay log with -FPSdata under logs/ (use `mark`)
          -h, --help             this text
        """;

    public static BridgeSettings Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var s = new BridgeSettings();
        var extra = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{arg} needs a value");
            switch (arg)
            {
                case "--name":
                    s = s with { Name = Next() };
                    break;
                case "--sink":
                    s = s with { Sink = Next() };
                    break;
                case "--uxplay-exe":
                    s = s with { UxPlayExe = Next() };
                    break;
                case "--uxplay-arg":
                    extra.Add(Next());
                    break;
                case "--uxplay-log":
                    s = s with { UxPlayLog = Next() };
                    break;
                case "--msys-bin":
                    s = s with { MsysBin = Next() };
                    break;
                case "--attach":
                    s = s with { Attach = i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : "uxplay" };
                    break;
                case "--no-follow":
                    s = s with { Follow = false };
                    break;
                case "--landscape-rot":
                    s = s with { LandscapeRotation = ParseRotation(Next()) };
                    break;
                case "--dry-run":
                    s = s with { DryRun = true };
                    break;
                case "-v" or "--verbose":
                    s = s with { Verbose = true };
                    break;
                case "--duration":
                    s = s with { DurationSeconds = double.Parse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture) };
                    break;
                case "--collect":
                    s = s with { Collect = true };
                    break;
                case "-h" or "--help":
                    s = s with { Help = true };
                    break;
                default:
                    throw new ArgumentException($"unknown option {arg}");
            }
        }

        if (s.Collect)
        {
            extra.Add("-FPSdata");
        }

        return s with { UxPlayArgs = extra };
    }

    /// <summary>Accepts 0 / 90 / 180 / 270, optionally prefixed with r.</summary>
    public static Rotation ParseRotation(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var digits = text.StartsWith('r') || text.StartsWith('R') ? text[1..] : text;
        if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var deg) && Enum.IsDefined((Rotation)deg))
        {
            return (Rotation)deg;
        }

        throw new ArgumentException($"rotation must be 0, 90, 180 or 270, not {text}");
    }
}
