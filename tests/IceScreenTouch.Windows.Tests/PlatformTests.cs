// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Windows.Tests;

public class MouseHookTranslateTests
{
    private static MsLlHookStruct At(int x, int y, int wheel = 0) => new()
    {
        Pt = new NativeMethods.Point { X = x, Y = y },
        MouseData = unchecked((uint)(wheel << 16)),
    };

    [Fact]
    public void ButtonsBecomePressAndRelease()
    {
        Assert.Equal(MouseInputEvent.Press(MouseButton.Left, new PointD(10, 20)), MouseHook.Translate(WmLButtonDown, At(10, 20)));
        Assert.Equal(MouseInputEvent.Release(MouseButton.Left, new PointD(10, 20)), MouseHook.Translate(WmLButtonUp, At(10, 20)));
        Assert.Equal(MouseInputEvent.Press(MouseButton.Right, new PointD(-5, 7)), MouseHook.Translate(WmRButtonDown, At(-5, 7)));
        Assert.Equal(MouseInputEvent.Release(MouseButton.Right, new PointD(-5, 7)), MouseHook.Translate(WmRButtonUp, At(-5, 7)));
    }

    [Fact]
    public void TheWheelDeltaIsTheSignedHighWord()
    {
        // Shift is not held while the tests run (the input tests release it).
        Assert.Equal(MouseInputEvent.Wheel(new PointD(1, 2), -120), MouseHook.Translate(WmMouseWheel, At(1, 2, -120)));
        Assert.Equal(MouseInputEvent.Wheel(new PointD(1, 2), 240), MouseHook.Translate(WmMouseWheel, At(1, 2, 240)));
        Assert.Equal(MouseInputEvent.Wheel(new PointD(1, 2), 60, horizontal: true), MouseHook.Translate(WmMouseHWheel, At(1, 2, 60)));
    }
}

public class ChildProcessJobTests
{
    [Fact]
    public void ClosingTheJobKillsItsProcesses()
    {
        using var process = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var job = new ChildProcessJob();
            job.Add(process);
            Assert.False(process.HasExited);

            job.Dispose();

            Assert.True(process.WaitForExit(3000), "the process outlived its job");
            job.Dispose(); // twice is fine
            Assert.Throws<ObjectDisposedException>(() => job.Add(process));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }
}
