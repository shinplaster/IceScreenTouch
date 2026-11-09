namespace IceScreenTouch.App.Services;

/// <summary>How the app would use a receiver it found.</summary>
public enum ReceiverCandidateKind
{
    /// <summary>A uxplay.exe the app starts itself (<see cref="Settings.ReceiverMode.StartUxPlay"/>).</summary>
    StartUxPlay,

    /// <summary>A receiver that runs on its own; the app follows its window (<see cref="Settings.ReceiverMode.AttachToProcess"/>).</summary>
    Attach,
}

/// <summary>An AirPlay receiver found on this PC.</summary>
/// <param name="Name">Name for the user ("AirServer").</param>
/// <param name="Kind">Start it or follow it.</param>
/// <param name="ProcessName">The process to follow (without .exe).</param>
/// <param name="ExePath">
/// The program, if known (to start it, or the uxplay.exe to use); for a receiver that is not a plain program, what
/// the platform starts it with (Windows: a <c>shell:AppsFolder\...</c> address for a Store app). It may start a
/// different process from <paramref name="ProcessName"/> (5KPlayer.exe starts Airplay.exe, which shows the picture).
/// </param>
/// <param name="Running">It runs now.</param>
public sealed record ReceiverCandidate(string Name, ReceiverCandidateKind Kind, string ProcessName, string? ExePath, bool Running);

/// <summary>Finds the AirPlay receivers on this PC (implemented by each platform head).</summary>
public interface IReceiverFinder
{
    /// <summary>The receivers found, the most useful first (may take a moment; call off the UI thread).</summary>
    IReadOnlyList<ReceiverCandidate> Find();

    /// <summary>Starts a receiver that is installed but not running. Returns false when it could not be started.</summary>
    bool Launch(ReceiverCandidate candidate);
}
