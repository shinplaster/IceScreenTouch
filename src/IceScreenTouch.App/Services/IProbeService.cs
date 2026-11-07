namespace IceScreenTouch.App.Services;

/// <summary>Which measurement to run on the phone.</summary>
public enum ProbeKind
{
    /// <summary>9 taps, a swipe and a pinch on the probe page (/): mapping, orientation, lift, latency.</summary>
    Touch,

    /// <summary>Wheel scrolls with the current settings on the scroll page (/scroll).</summary>
    Scroll,

    /// <summary>Touch -> the change on the mirror window, on the latency page (/latency).</summary>
    Latency,

    /// <summary>Typing over the HID keyboard into the text box of the keyboard page (/keys).</summary>
    Keys,
}

/// <summary>Where a probe run is.</summary>
public enum ProbePhase
{
    /// <summary>Nothing running.</summary>
    Idle,

    /// <summary>Starting the page server.</summary>
    Starting,

    /// <summary>Waiting for the iPhone (subscription) and for the page in Safari.</summary>
    Waiting,

    /// <summary>About to start (a short countdown; the mouse no longer reaches the phone).</summary>
    Countdown,

    /// <summary>Sending touches; the mouse does not reach the phone meanwhile.</summary>
    Running,

    /// <summary>Finished, every check passed (or the scroll measurement completed).</summary>
    Passed,

    /// <summary>Finished, some checks failed (or nothing reached the page).</summary>
    Failed,

    /// <summary>Cancelled by the user (or the session stopped).</summary>
    Cancelled,

    /// <summary>Could not run (no Bluetooth, port in use, ...).</summary>
    Error,
}

/// <summary>What the phone or the page still has to do before a probe can start (none = ready).</summary>
[Flags]
public enum ProbeWaitingFor
{
    /// <summary>Ready.</summary>
    None = 0,

    /// <summary>The iPhone has not subscribed to the touch report (Bluetooth).</summary>
    Bluetooth = 1,

    /// <summary>The page has not been opened in Safari.</summary>
    PageNotOpened = 2,

    /// <summary>The page is not in the foreground.</summary>
    PageNotInFront = 4,

    /// <summary>The other probe page is open.</summary>
    WrongPage = 8,

    /// <summary>Latency: the middle of the mirror picture is not on screen (no picture, covered, or not the black page).</summary>
    MirrorHidden = 16,

    /// <summary>Keys: the text box on the page does not have the cursor.</summary>
    FieldNotFocused = 32,
}

/// <summary>One pass/fail item of a probe.</summary>
public sealed record ProbeCheckItem(string Name, bool Ok, string Detail);

/// <summary>A progress report from a running probe. <see cref="Line"/> is one line of the detailed output, if any.</summary>
public sealed record ProbeUpdate
{
    /// <summary>The phase now.</summary>
    public ProbePhase Phase { get; init; }

    /// <summary>While <see cref="ProbePhase.Waiting"/>: what is missing.</summary>
    public ProbeWaitingFor WaitingFor { get; init; }

    /// <summary>While <see cref="ProbePhase.Countdown"/>: seconds left.</summary>
    public int SecondsLeft { get; init; }

    /// <summary>The page URLs (once the server is up).</summary>
    public IReadOnlyList<string>? Urls { get; init; }

    /// <summary>A line of the detailed output (English, like the console tool).</summary>
    public string? Line { get; init; }
}

/// <summary>The outcome of a probe run.</summary>
/// <param name="Phase"><see cref="ProbePhase.Passed"/>, <see cref="ProbePhase.Failed"/>, <see cref="ProbePhase.Cancelled"/> or <see cref="ProbePhase.Error"/>.</param>
/// <param name="Summary">One line for the user (Japanese).</param>
/// <param name="Checks">The pass/fail items (touch probe) or the measured rows (scroll).</param>
public sealed record ProbeReport(ProbePhase Phase, string Summary, IReadOnlyList<ProbeCheckItem> Checks);

/// <summary>State of the firewall rule that lets the iPhone reach the probe page.</summary>
public enum FirewallState
{
    /// <summary>Not checked (or cannot be checked on this OS).</summary>
    Unknown,

    /// <summary>A rule allows the probe port from the local subnet only.</summary>
    Allowed,

    /// <summary>No rule allows the probe port (Windows will ask, or block).</summary>
    Missing,

    /// <summary>A rule allows the port from anywhere (works, but broader than needed).</summary>
    TooBroad,

    /// <summary>
    /// The app is installed as a package (Microsoft Store build) and a rule allows the port. The package's rule
    /// comes from its manifest and cannot be limited to the local subnet, so it is accepted as it is.
    /// </summary>
    AllowedByPackage,
}

/// <summary>
/// Runs the touch probe inside the app (implemented by each platform head). It borrows the session's
/// Bluetooth peripheral and pauses the mouse bridge while it sends.
/// </summary>
public interface IProbeService
{
    /// <summary>The TCP port the probe page is served on.</summary>
    int Port { get; }

    /// <summary>The URLs the page will be reachable at (one per LAN address), before a run starts.</summary>
    IReadOnlyList<string> PageUrls(ProbeKind kind);

    /// <summary>
    /// Serves the page, waits for the phone and the page, runs the measurement, and reports. Expected failures
    /// are returned as <see cref="ProbePhase.Error"/>; cancelling returns <see cref="ProbePhase.Cancelled"/>.
    /// </summary>
    Task<ProbeReport> RunAsync(ProbeKind kind, IProgress<ProbeUpdate> progress, CancellationToken cancellationToken);

    /// <summary>
    /// True when the firewall rule comes with the installed package (Microsoft Store build): the "set up the
    /// firewall" button is then never offered, a missing rule is fixed by repairing or reinstalling the app.
    /// </summary>
    bool FirewallFromPackage { get; }

    /// <summary>Looks for the firewall rule (may take a moment; call off the UI thread).</summary>
    Task<FirewallState> CheckFirewallAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the firewall rule (local subnet only) and removes broader rules for this program. Asks for
    /// administrator rights. Returns false when the user declined or it failed (see the log).
    /// </summary>
    Task<bool> AllowFirewallAsync(CancellationToken cancellationToken = default);
}
