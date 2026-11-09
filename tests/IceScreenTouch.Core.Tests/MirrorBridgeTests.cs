using System.Threading.Channels;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

/// <summary>A desktop with one mirror window whose geometry the test controls.</summary>
internal sealed class FakeDesktop : IDesktop
{
    public const nint MirrorWindow = 0x1234;
    public const nint OtherWindow = 0x5678;

    public PointD CursorPosition { get; set; }

    public HashSet<ShortcutKey> KeysDown { get; } = [];

    public nint Foreground { get; set; } = MirrorWindow;

    /// <summary>Top-level window under a point; the mirror window everywhere by default.</summary>
    public Func<PointD, nint> WindowAt { get; set; } = _ => MirrorWindow;

    public RectI? Client { get; set; } = new RectI(300, 150, 400, 600);

    public WindowMetrics? Metrics { get; set; } = new WindowMetrics(16, 39, new RectI(0, 0, 2560, 1400));

    public List<(nint Window, int Width, int Height)> Resizes { get; } = [];

    public bool IsKeyDown(ShortcutKey key) => KeysDown.Contains(key);

    /// <summary>When set, the desktop counts shortcut presses like the keyboard hook (<see cref="Presses"/>).</summary>
    public bool CountsPresses { get; set; }

    public Dictionary<ShortcutKey, int> Presses { get; } = [];

    public int? TakeShortcutPresses(ShortcutKey key)
    {
        if (!CountsPresses)
        {
            return null;
        }

        var count = Presses.GetValueOrDefault(key);
        Presses[key] = 0;
        return count;
    }

    public bool IsForeground(nint window) => window == Foreground;

    public bool IsVisibleAt(nint window, PointD screen) => WindowAt(screen) == window;

    public RectI? GetClientRect(nint window) => window == MirrorWindow ? Client : null;

    public WindowMetrics? GetMetrics(nint window) => Metrics;

    public void SetClientSize(nint window, int width, int height)
    {
        Resizes.Add((window, width, height));
        if (Client is { } c)
        {
            Client = c with { Width = width, Height = height };
        }
    }
}

internal sealed class FakeVideo : IVideoSource
{
    public string Name => "fake";

    public nint Window { get; set; } = FakeDesktop.MirrorWindow;

    public SizeD? VideoSize { get; set; } = new SizeD(498, 1080);

    public int FindCalls { get; private set; }

    /// <summary>Where the picture is in the client area (a receiver window that is looked at), or null.</summary>
    public Func<nint, SizeD, RectD?>? Locate { get; set; }

    public nint FindWindow()
    {
        FindCalls++;
        return Window;
    }

    public RectD? LocatePicture(nint window, SizeD client) => Locate?.Invoke(window, client);

    /// <summary>When set, <see cref="WindowSize"/> answers <see cref="AskedWindowSize"/> instead of the video size.</summary>
    public bool AsksWindowSize { get; set; }

    public SizeD? AskedWindowSize { get; set; }

    public SizeD? WindowSize => AsksWindowSize ? AskedWindowSize : VideoSize;
}

// The bridge part of poc/poc3-integrated/test_bridge.py, with a fake desktop and clock instead of Win32 input.
public class MirrorBridgeTests
{
    private static readonly string[] TapFrames = ["down", "lift", "empty"];

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingSink _sink;
    private readonly FakeDesktop _desktop = new();
    private readonly FakeVideo _video = new();
    private readonly Channel<MouseInputEvent> _buttons = Channel.CreateUnbounded<MouseInputEvent>();
    private readonly List<string> _log = [];
    private MirrorBridge _bridge;

    public MirrorBridgeTests()
    {
        _sink = new RecordingSink(_time);
        _bridge = CreateBridge(follow: false);
    }

    // Client 400x600 at (300,150), picture 498x1080 letterboxed: 276.67 x 600 centered horizontally.
    private static RectD ExpectedPortrait => Geometry.VideoRect(400, 600, new SizeD(498, 1080)).Offset(300, 150);

    private MirrorBridge CreateBridge(bool follow) =>
        new(new TouchGestureEngine(_sink, _time), _video, _desktop, _buttons.Reader, new BridgeOptions { FollowVideoSize = follow }, _time, _log.Add);

    private static PointD At(RectD r, double u, double v) => new(r.Left + (r.Width * u), r.Top + (r.Height * v));

    private static string Kind(Frame f) => f.IsEmpty ? "empty" : f.IsDown ? "down" : "lift";

    private Task Tick() => _time.Drive(_bridge.TickAsync());

    private void Press(PointD p, bool ctrl = false)
    {
        _desktop.CursorPosition = p;
        _buttons.Writer.TryWrite(MouseInputEvent.Press(MouseButton.Left, p, ctrl));
    }

    private void Release(PointD p)
    {
        _desktop.CursorPosition = p;
        _buttons.Writer.TryWrite(MouseInputEvent.Release(MouseButton.Left, p));
    }

    private async Task Click(PointD p, bool ctrl = false)
    {
        Press(p, ctrl);
        Release(p);
        await Tick();
    }

    /// <summary>Press, move the cursor along the path one tick per point, release at the end.</summary>
    private async Task Drag(PointD[] path, double stepMs = 30, bool ctrl = false)
    {
        Press(path[0], ctrl);
        await Tick();
        foreach (var p in path.Skip(1))
        {
            _time.Wait(stepMs);
            _desktop.CursorPosition = p;
            await Tick();
        }

        Release(path[^1]);
        await Tick();
    }

    [Fact]
    public async Task ThePictureIsLetterboxedInsideTheClientArea()
    {
        await Tick();

        Assert.Equal(FakeDesktop.MirrorWindow, _bridge.Window);
        Assert.Equal(ExpectedPortrait, _bridge.Picture);
    }

    [Fact]
    public async Task WithoutAReportedSizeTheConfiguredScreenShapeIsAssumed()
    {
        // An iPad Air (820x1180 points) before the receiver reports a size.
        _video.VideoSize = null;
        _bridge = new(new TouchGestureEngine(_sink, _time), _video, _desktop, _buttons.Reader, new BridgeOptions { FollowVideoSize = false, FallbackVideoSize = new SizeD(820, 1180) }, _time, _log.Add);

        await Tick();

        Assert.Equal(Geometry.VideoRect(400, 600, new SizeD(820, 1180)).Offset(300, 150), _bridge.Picture);
    }

    [Fact]
    public async Task APictureLocatedByTheVideoSourceWinsOverTheReportedSize()
    {
        SizeD? asked = null;
        _video.Locate = (window, client) =>
        {
            asked = client;
            return window == FakeDesktop.MirrorWindow ? new RectD(0, 100, 400, 184.5) : null; // landscape, top-aligned
        };

        await Click(new PointD(400, 300));

        Assert.Equal(new SizeD(400, 600), asked);
        Assert.Equal(new RectD(300, 250, 400, 184.5), _bridge.Picture);
        Assert.True(_bridge.Status.IsLandscape);
        Assert.Equal(Geometry.ScreenToAxis(new PointD(400, 300), new RectD(300, 250, 400, 184.5), Rotation.R90), _sink.Frames[0].First);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    public async Task APressMadeWithAltHeldIsNotATouch(MouseButton button)
    {
        var p = At(ExpectedPortrait, 0.5, 0.5);
        _desktop.CursorPosition = p;
        _buttons.Writer.TryWrite(MouseInputEvent.Press(button, p, alt: true));
        await Tick();
        _buttons.Writer.TryWrite(MouseInputEvent.Release(button, p));
        await Tick();

        Assert.False(_bridge.Engine.IsDown);
        Assert.Empty(_sink.Frames);
        Assert.Equal(0, _bridge.Status.Gestures);
    }

    [Fact]
    public async Task AListenerThatThrowsDoesNotEndTheTickNorFloodTheLog()
    {
        _bridge.StatusPublished += _ => throw new InvalidOperationException("listener");

        await Tick();
        await Tick();

        Assert.Single(_log, l => l.Contains("status listener failed", StringComparison.Ordinal));
        Assert.Equal(FakeDesktop.MirrorWindow, _bridge.Status.Window);
    }

    [Fact]
    public async Task EveryTickPublishesItsStatusToAListener()
    {
        var seen = new List<BridgeStatus>();
        _bridge.StatusPublished += seen.Add;

        await Tick();
        await Tick();

        Assert.Equal(2, seen.Count);
        Assert.Equal(FakeDesktop.MirrorWindow, seen[0].Window);
        Assert.Equal(ExpectedPortrait, seen[0].Picture);
        Assert.Same(_bridge.Status, seen[1]);
    }

    [Fact]
    public async Task TapInTheMiddleOfThePictureIsDownLiftEmptyAtTheCenter()
    {
        await Click(At(ExpectedPortrait, 0.5, 0.5));

        Assert.Equal(TapFrames, _sink.Frames.Select(Kind));
        Assert.Equal(new AxisPoint(5000, 5000), _sink.Frames[0].First);
        Assert.Equal(new AxisPoint(5000, 5000), _sink.Frames[1].First); // lifted where it was, never at (0,0)
    }

    [Fact]
    public async Task DragFromAQuarterToThreeQuartersIsMonotonicAndEndsWithALift()
    {
        var pic = ExpectedPortrait;
        var path = Enumerable.Range(0, 9).Select(i => At(pic, 0.25 + (0.5 * i / 8), 0.25 + (0.5 * i / 8))).ToArray();

        await Drag(path);

        var downs = _sink.Downs.Select(f => f.First).ToList();
        Assert.True(downs.Count >= 6, $"{downs.Count} frames");
        Assert.All(downs.Zip(downs.Skip(1)), p => Assert.True(p.Second.X >= p.First.X && p.Second.Y >= p.First.Y));
        Assert.InRange(downs[0].X, 2490, 2510);
        Assert.InRange(downs[^1].X, 7490, 7510);
        Assert.InRange(downs[^1].Y, 7490, 7510);
        Assert.Equal(["lift", "empty"], _sink.Frames.TakeLast(2).Select(Kind));
    }

    [Fact]
    public async Task AClickInTheLetterboxBarIsIgnored()
    {
        await Click(new PointD(305, 450)); // inside the client area, left of the picture

        Assert.Empty(_sink.Frames);
        Assert.Equal(0, _bridge.Engine.Stats.Dropped);
    }

    [Fact]
    public async Task ADragEnteringThePictureFromOutsideIsIgnored()
    {
        await Drag([new PointD(305, 450), At(ExpectedPortrait, 0.5, 0.5)]);

        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task ADragLeavingThePictureClampsToTheEdge()
    {
        var pic = ExpectedPortrait;
        await Drag([At(pic, 0.5, 0.5), new PointD(pic.Left + pic.Width + 60, pic.Top + (pic.Height / 2))]);

        Assert.Equal(HidConstants.AxisMax, _sink.Downs.Last().First.X);
        Assert.Equal("empty", Kind(_sink.Frames[^1]));
    }

    [Fact]
    public async Task APressWhereAnotherWindowCoversThePictureIsIgnored()
    {
        _desktop.WindowAt = _ => FakeDesktop.OtherWindow;

        await Click(At(ExpectedPortrait, 0.5, 0.5));

        Assert.Empty(_sink.Frames);
        Assert.Equal(0, _bridge.Engine.Stats.Dropped);
    }

    [Fact]
    public async Task APressWhileThePhoneIsNotSubscribedIsCountedAsDropped()
    {
        _sink.IsSubscribed = false;

        await Click(At(ExpectedPortrait, 0.5, 0.5));

        Assert.Empty(_sink.Frames);
        Assert.Equal(1, _bridge.Engine.Stats.Dropped);
    }

    [Fact]
    public async Task CtrlDragSendsTwoContactsMirroredThroughTheCenter()
    {
        var pic = ExpectedPortrait;
        await Drag([At(pic, 0.6, 0.5), At(pic, 0.8, 0.5)], ctrl: true);

        var pinch = _sink.Downs.ToList();
        Assert.NotEmpty(pinch);
        Assert.All(pinch, f =>
        {
            Assert.Equal(2, f.Contacts.Length);
            Assert.Equal(HidConstants.AxisMax, f.Contacts[0].X + f.Contacts[1].X);
            Assert.Equal(HidConstants.AxisMax, f.Contacts[0].Y + f.Contacts[1].Y);
        });
    }

    [Fact]
    public async Task FastClicksOnASlowLinkAreAllDeliveredAndEachIsHeldTheMinimum()
    {
        // The reported PoC symptom: clicks must not be lost while sends are slow. All 8 clicks arrive
        // in the channel before the loop gets to them.
        _sink.SendDelay = TimeSpan.FromMilliseconds(60);
        var center = At(ExpectedPortrait, 0.5, 0.5);
        for (var i = 0; i < 8; i++)
        {
            Press(center);
            Release(center);
        }

        await Tick();

        var kinds = _sink.Frames.Select(Kind).ToList();
        Assert.Equal(Enumerable.Repeat(TapFrames, 8).SelectMany(k => k), kinds);
        var holds = _sink.Frames.Where(f => f.IsDown).Zip(_sink.Frames.Where(f => f.IsLift), (d, l) => l.At - d.At);
        Assert.All(holds, h => Assert.True(h >= TimeSpan.FromMilliseconds(70), $"held {h.TotalMilliseconds} ms"));
        Assert.Equal(8, _bridge.Engine.Stats.Gestures);
    }

    [Fact]
    public async Task AFastSweepIsRateLimitedButEndsAtTheFinalPosition()
    {
        var pic = ExpectedPortrait;
        Press(At(pic, 0.2, 0.5));
        await Tick();
        for (var i = 1; i <= 60; i++)
        {
            _time.Wait(2); // 60 positions in 120 ms
            _desktop.CursorPosition = At(pic, 0.2 + (0.6 * i / 60), 0.5);
            await Tick();
        }

        var final = At(pic, 0.8, 0.5);
        Release(final);
        await Tick();

        var downs = _sink.Downs.ToList();
        Assert.InRange(downs.Count, 2, (120 / 16) + 3);
        Assert.Equal(Geometry.ScreenToAxis(final, pic, Rotation.R0), downs[^1].First);
    }

    [Fact]
    public async Task LosingTheWindowMidDragLiftsTheFingerAndDropsPendingClicks()
    {
        Press(At(ExpectedPortrait, 0.5, 0.5));
        await Tick();
        _video.Window = 0;
        Release(At(ExpectedPortrait, 0.5, 0.5));
        Press(At(ExpectedPortrait, 0.3, 0.3));

        await Tick();

        Assert.False(_bridge.Engine.IsDown);
        Assert.Null(_bridge.Picture);
        Assert.Equal(TapFrames, _sink.Frames.Select(Kind));
        Assert.False(_buttons.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AWindowThatCannotBeMeasuredIsTreatedLikeNoWindow()
    {
        _desktop.Client = null;
        Press(At(ExpectedPortrait, 0.5, 0.5));

        await Tick();

        Assert.Null(_bridge.Picture);
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task WithoutAReportedSizeThePortraitFallbackIsUsed()
    {
        _video.VideoSize = null;

        await Tick();

        Assert.Equal(Geometry.VideoRect(400, 600, Geometry.FallbackVideoSize).Offset(300, 150), _bridge.Picture);
    }

    [Fact]
    public async Task TheWindowFollowsThePictureSizeOncePerChange()
    {
        _bridge = CreateBridge(follow: true);

        await Tick();
        await Tick();
        _video.VideoSize = new SizeD(1920, 886); // the phone turned to landscape
        await Tick();
        await Tick();
        _video.VideoSize = new SizeD(498, 1080);
        await Tick();

        // Landscape is scaled down to 90% of the work area minus the frame: (2560*0.9-16)=2288 > 1920, (1400*0.9-39)=1221 > 886.
        Assert.Equal([(FakeDesktop.MirrorWindow, 498, 1080), (FakeDesktop.MirrorWindow, 1920, 886), (FakeDesktop.MirrorWindow, 498, 1080)], _desktop.Resizes);

        // After following there are no letterbox bars.
        Assert.Equal(new RectD(300, 150, 498, 1080), _bridge.Picture);
    }

    [Fact]
    public async Task ANewWindowIsResizedEvenIfThePictureSizeDidNotChange()
    {
        _bridge = CreateBridge(follow: true);
        await Tick();
        _video.Window = 0;
        await Tick();
        _video.Window = FakeDesktop.MirrorWindow; // e.g. uxplay recreated its window after a reconnect
        await Tick();

        Assert.Equal(2, _desktop.Resizes.Count);
    }

    [Fact]
    public async Task AReceiverWithoutAVideoSizeCanAskForAWindowShape()
    {
        _video.VideoSize = null;
        _video.AsksWindowSize = true;
        _bridge = CreateBridge(follow: true);

        await Tick(); // nothing asked: the window is left alone
        _video.AskedWindowSize = new SizeD(1080, 498); // a running uxplay letterboxes a landscape picture
        await Tick();
        await Tick();

        Assert.Equal([(FakeDesktop.MirrorWindow, 1080, 498)], _desktop.Resizes);
    }

    [Fact]
    public async Task WithoutFollowTheWindowIsNeverResized()
    {
        _video.VideoSize = new SizeD(1920, 886);

        await Tick();

        Assert.Empty(_desktop.Resizes);
    }

    [Fact]
    public async Task ALandscapeClickIsRotatedIntoThePortraitFrame()
    {
        _video.VideoSize = new SizeD(1920, 886);
        _desktop.Client = new RectI(300, 150, 1920, 886);
        var pic = new RectD(300, 150, 1920, 886);
        _bridge.Engine.LandscapeRotation = Rotation.R90;

        await Click(At(pic, 0.75, 0.25));

        // 90: (u, v) -> (1 - v, u)
        Assert.Equal(new AxisPoint(7500, 7500), _sink.Frames[0].First);
    }

    [Fact]
    public async Task HomeKeyWhileFocusedSendsOneSwipePerPress()
    {
        _desktop.KeysDown.Add(ShortcutKey.Home);
        await Tick();
        await Tick(); // still held: no second swipe
        _desktop.KeysDown.Clear();
        await Tick();

        var downs = _sink.Downs.ToList();
        Assert.Equal(new AxisPoint(5000, 9950), downs[0].First);
        Assert.Equal(new AxisPoint(5000, 6500), downs[^1].First);
        Assert.Single(_sink.Frames, f => f.IsLift);
    }

    [Fact]
    public async Task ASecondHomePressMadeWhileTheFirstWasBeingSentFiresToo()
    {
        // The hook counts every press. The bridge was busy sending the first one when the key went up and down again:
        // at the next look the key is held and one press is counted, which is not the old hold (review, 2026-11-07).
        _desktop.CountsPresses = true;
        _desktop.KeysDown.Add(ShortcutKey.Home);
        _desktop.Presses[ShortcutKey.Home] = 1;
        await Tick();
        Assert.Single(_sink.Frames, f => f.IsLift);

        _desktop.Presses[ShortcutKey.Home] = 1; // released and pressed again meanwhile: still held
        await Tick();

        Assert.Equal(2, _sink.Frames.Count(f => f.IsLift));
    }

    [Fact]
    public async Task APressSeenAsHeldBeforeItWasCountedFiresOnce()
    {
        // The hook sets the key state before it counts the press: one look can see the key held with nothing counted.
        _desktop.CountsPresses = true;
        _desktop.KeysDown.Add(ShortcutKey.Home);
        await Tick();
        Assert.Empty(_sink.Frames);

        _desktop.Presses[ShortcutKey.Home] = 1;
        await Tick();
        await Tick(); // still held, nothing new counted
        _desktop.KeysDown.Clear();
        await Tick();

        Assert.Single(_sink.Frames, f => f.IsLift);
    }

    [Fact]
    public async Task ACountedHomePressMadeWhileTheMirrorHadNoFocusDoesNotFireLater()
    {
        _desktop.CountsPresses = true;
        _desktop.Foreground = FakeDesktop.OtherWindow;
        _desktop.Presses[ShortcutKey.Home] = 1;
        await Tick();

        _desktop.Foreground = FakeDesktop.MirrorWindow;
        await Tick();

        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task ASecondRotatePressMadeWhileBusyTogglesAgain()
    {
        _video.VideoSize = new SizeD(1920, 886);
        var changes = new List<Rotation>();
        _bridge.LandscapeRotationChanged += (_, r) => changes.Add(r);
        _desktop.CountsPresses = true;

        _desktop.KeysDown.Add(ShortcutKey.Rotate);
        _desktop.Presses[ShortcutKey.Rotate] = 1;
        await Tick();
        _desktop.Presses[ShortcutKey.Rotate] = 1; // up and down again between two looks; held at both
        await Tick();

        Assert.Equal([Rotation.R270, Rotation.R90], changes);
    }

    [Fact]
    public async Task HomeKeyIsIgnoredWhenTheMirrorWindowHasNoFocus()
    {
        _desktop.Foreground = FakeDesktop.OtherWindow;
        _desktop.KeysDown.Add(ShortcutKey.Home);

        await Tick();

        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task HomeInLandscapeSwipesAlongTheRotatedBottomEdge()
    {
        _video.VideoSize = new SizeD(1920, 886);
        _desktop.KeysDown.Add(ShortcutKey.Home);

        await Tick();

        var downs = _sink.Downs.ToList();
        Assert.Equal(new AxisPoint(50, 5000), downs[0].First);
        Assert.Equal(new AxisPoint(3500, 5000), downs[^1].First);
    }

    [Fact]
    public async Task RotateKeyTogglesTheLandscapeRotationOncePerPress()
    {
        _video.VideoSize = new SizeD(1920, 886);
        var changes = new List<Rotation>();
        _bridge.LandscapeRotationChanged += (_, r) => changes.Add(r);

        _desktop.KeysDown.Add(ShortcutKey.Rotate);
        await Tick();
        await Tick();
        _desktop.KeysDown.Clear();
        await Tick();
        _desktop.KeysDown.Add(ShortcutKey.Rotate);
        await Tick();

        Assert.Equal([Rotation.R270, Rotation.R90], changes);
        Assert.Equal(Rotation.R90, _bridge.Engine.LandscapeRotation);
        Assert.Contains(_log, l => l.Contains("landscape rotation = 270", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RotateKeyInPortraitTurnsThePictureUpsideDownWithoutTouchingTheLandscapeSetting()
    {
        // An iPad held upside down shows the same portrait picture: Ctrl+R flips 0 <-> 180 (2026-11-06).
        var changes = new List<Rotation>();
        _bridge.LandscapeRotationChanged += (_, r) => changes.Add(r);

        _desktop.KeysDown.Add(ShortcutKey.Rotate);
        await Tick();
        _desktop.KeysDown.Clear();
        await Tick();

        Assert.Equal(Rotation.R180, _bridge.Engine.PortraitRotation);
        Assert.Equal(Rotation.R180, _bridge.Status.PortraitRotation);
        Assert.Equal(Rotation.R90, _bridge.Engine.LandscapeRotation);
        Assert.Empty(changes); // not saved
        Assert.Contains(_log, l => l.Contains("portrait rotation = 180", StringComparison.Ordinal));

        // A click near the top-left lands near the device's bottom-right.
        await Click(At(ExpectedPortrait, 0.25, 0.1));
        Assert.Equal(new AxisPoint(7500, 9000), _sink.Frames[0].First);

        // The command (the Rotate button) does the same for a portrait picture.
        _bridge.Post(new BridgeCommand(BridgeCommandKind.ToggleRotation));
        await Tick();
        Assert.Equal(Rotation.R0, _bridge.Engine.PortraitRotation);
    }

    [Fact]
    public async Task RotateKeyIsIgnoredWhenTheMirrorWindowHasNoFocus()
    {
        _desktop.Foreground = 0;
        _desktop.KeysDown.Add(ShortcutKey.Rotate);

        await Tick();

        Assert.Equal(Rotation.R90, _bridge.Engine.LandscapeRotation);
    }

    [Fact]
    public async Task SuspendLiftsAFingerThatIsDownAndCompletesOnTheNextTick()
    {
        Press(At(ExpectedPortrait, 0.5, 0.5));
        await Tick();
        Assert.True(_bridge.Engine.IsDown);

        var suspended = _bridge.SuspendAsync();
        Assert.False(suspended.IsCompleted);
        await Tick();

        await suspended.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_bridge.Engine.IsDown);
        Assert.True(_bridge.Status.Suspended);
        Assert.Equal(["lift", "empty"], _sink.Frames.TakeLast(2).Select(Kind));
    }

    [Fact]
    public async Task WhileSuspendedClicksKeysAndCommandsAreDropped()
    {
        var suspended = _bridge.SuspendAsync();
        await Tick();
        await suspended;

        await Click(At(ExpectedPortrait, 0.5, 0.5));
        _desktop.KeysDown.Add(ShortcutKey.Rotate);
        _bridge.Post(new BridgeCommand(BridgeCommandKind.Home));
        await Tick();

        Assert.Empty(_sink.Frames);
        Assert.Equal(Rotation.R90, _bridge.Engine.LandscapeRotation);
        Assert.Contains(_log, l => l.Contains("Home ignored while suspended", StringComparison.Ordinal));

        // The key is still held when resuming: it must not fire as a new press.
        _bridge.Resume();
        await Tick();
        Assert.False(_bridge.Status.Suspended);
        Assert.Equal(Rotation.R90, _bridge.Engine.LandscapeRotation);
        _desktop.KeysDown.Clear();

        await Click(At(ExpectedPortrait, 0.5, 0.5));
        Assert.Equal(TapFrames, _sink.Frames.Select(Kind));
    }

    [Fact]
    public async Task SuspendNestsAndASecondSuspendWhileSuspendedCompletesAtOnce()
    {
        var first = _bridge.SuspendAsync();
        await Tick();
        await first;

        var second = _bridge.SuspendAsync();
        Assert.True(second.IsCompletedSuccessfully);

        _bridge.Resume();
        await Tick();
        Assert.True(_bridge.Status.Suspended);

        _bridge.Resume();
        _bridge.Resume(); // extra calls are ignored
        await Tick();
        Assert.False(_bridge.Status.Suspended);
        Assert.Contains("[bridge] resumed", _log);
    }

    [Fact]
    public async Task SuspendCanBeCancelledWhenTheLoopDoesNotRun()
    {
        using var cts = new CancellationTokenSource();
        var suspended = _bridge.SuspendAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspended);
        _bridge.Resume();
        await Tick();
        Assert.False(_bridge.Status.Suspended);
    }

    [Fact]
    public async Task RunAsyncTicksOnTheTimerUntilCancelled()
    {
        using var cts = new CancellationTokenSource();
        var run = _bridge.RunAsync(cts.Token);

        for (var i = 0; i < 10; i++)
        {
            _time.Wait(4);
        }

        await cts.CancelAsync();
        await run;

        Assert.InRange(_video.FindCalls, 9, 10);
    }
}
