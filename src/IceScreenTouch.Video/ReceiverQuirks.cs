// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Video;

/// <summary>What is known about particular receivers whose windows are followed (by process name, without .exe).</summary>
public static class ReceiverQuirks
{
    /// <summary>
    /// The receiver fits its window to the picture by itself after a rotation (AirServer): reshaping it too made
    /// it flip between portrait and landscape (an iPad, 2026-11-05).
    /// </summary>
    public static bool ReshapesItsOwnWindow(string processName) => Is(processName, "AirServer");

    /// <summary>
    /// Height in pixels at 100 % display scale of a bar the receiver draws across the bottom of its client area,
    /// outside the picture: LonelyScreen's toolbar (16 px with the "^" button, under the picture; 2026-11-06).
    /// </summary>
    public static int BottomToolbar(string processName) => Is(processName, "LonelyScreen") ? 16 : 0;

    /// <summary>
    /// The receiver draws the picture with uneven margins, so the measured picture is better than a centered one
    /// (LonelyScreen: <see cref="Core.PictureLocatorOptions.TrustMeasuredShape"/>).
    /// </summary>
    public static bool UnevenMargins(string processName) => Is(processName, "LonelyScreen");

    /// <summary>
    /// The receiver keeps its window open without a device, showing a standby screen that can pass for a portrait
    /// picture (LonelyScreen's television and toolbar, 2026-11-06): its window is the mirror window only while a
    /// device is connected (<see cref="WindowVideoSourceOptions.OnlyWhileConnected"/>).
    /// </summary>
    public static bool ShowsStandbyScreen(string processName) => Is(processName, "LonelyScreen");

    /// <summary>
    /// A left-button drag on the picture moves the receiver's own window (Reflector 4 and 5KPlayer's Airplay.exe,
    /// 2026-11-09), so a swipe or a drag-scroll would carry the window along. The app keeps the button presses over
    /// the picture from such a receiver (<see cref="Platform.Windows.ClickSwallower"/>). The window can still be
    /// moved by its title bar or border, where there is one.
    /// </summary>
    public static bool MovesWindowOnDrag(string processName) => Is(processName, "Reflector4") || Is(processName, "Airplay");

    private static bool Is(string processName, string name)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var p = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return p.Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}
