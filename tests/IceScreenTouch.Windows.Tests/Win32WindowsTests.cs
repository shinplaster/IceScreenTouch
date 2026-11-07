using System.Diagnostics;
using System.Drawing;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Windows.Tests;

// Win32 window queries against a real window, checked with WinForms' own geometry. No input is injected.
public sealed class Win32WindowsTests : IDisposable
{
    private readonly StandInWindow _window = StandInWindow.Show(new Rectangle(320, 180, 400, 600));

    public void Dispose() => _window.Dispose();

    [Fact]
    public void TheClientRectangleMatchesWinForms()
    {
        var expected = _window.ClientOnScreen;

        var rc = Win32Windows.ClientRectOnScreen(_window.Handle);

        Assert.Equal(new RectI(expected.X, expected.Y, expected.Width, expected.Height), rc);
    }

    [Fact]
    public void TheLargestVisibleWindowOfThisProcessIsTheStandIn()
    {
        Assert.Equal(_window.Handle, Win32Windows.LargestVisibleWindowOf(Environment.ProcessId));
    }

    [Fact]
    public void AProcessWithoutWindowsHasNone()
    {
        Assert.Equal(0, Win32Windows.LargestVisibleWindowOf(int.MaxValue - 1));
    }

    [Fact]
    public void MetricsGiveTheFrameAndTheWorkArea()
    {
        var m = Win32Windows.GetMetrics(_window.Handle);

        var frame = _window.Frame;
        var work = _window.WorkingArea;
        Assert.Equal(new WindowMetrics(frame.Width, frame.Height, new RectI(work.X, work.Y, work.Width, work.Height)), m);
    }

    [Fact]
    public void SetClientSizeResizesTheClientAreaAndKeepsTheCorner()
    {
        var before = _window.ClientOnScreen;

        Assert.True(Win32Windows.SetClientSize(_window.Handle, 300, 500));

        var after = _window.ClientOnScreen;
        Assert.Equal(new Size(300, 500), after.Size);
        Assert.Equal(before.Location, after.Location);
    }

    [Fact]
    public void TheDesktopSeesTheWindowAtItsCenterButNotOutside()
    {
        var desktop = new Win32Desktop();
        var c = _window.ClientOnScreen;

        var at = Win32Windows.RootWindowAt(c.X + (c.Width / 2), c.Y + (c.Height / 2));
        Assert.True(desktop.IsVisibleAt(_window.Handle, new PointD(c.X + (c.Width / 2), c.Y + (c.Height / 2))), $"root at center {at:X} pid {Win32Windows.ProcessIdOf(at)} rect {Win32Windows.WindowRectOnScreen(at)}; stand-in {_window.Handle:X} root {Win32Windows.RootOf(_window.Handle):X} client {c}");
        Assert.False(desktop.IsVisibleAt(_window.Handle, new PointD(c.Right + 200, c.Y + (c.Height / 2))));
    }

    [Fact]
    public void ACoveringWindowHidesThePictureThere()
    {
        var desktop = new Win32Desktop();
        var c = _window.ClientOnScreen;
        using var cover = StandInWindow.Show(new Rectangle(c.X + 50, c.Y + 50, 100, 100), color: Color.DarkRed);

        Assert.False(desktop.IsVisibleAt(_window.Handle, new PointD(c.X + 100, c.Y + 100)));
        Assert.True(desktop.IsVisibleAt(_window.Handle, new PointD(c.X + 300, c.Y + 400)));
    }

    [Fact]
    public void TheVideoSourceForThisProcessFindsTheStandIn()
    {
        using var self = Process.GetCurrentProcess();
        using var source = new Video.WindowVideoSource(self.ProcessName, new Video.WindowVideoSourceOptions { DetectPicture = false });

        Assert.Equal(_window.Handle, source.FindWindow());
        Assert.Null(source.VideoSize);
    }
}
