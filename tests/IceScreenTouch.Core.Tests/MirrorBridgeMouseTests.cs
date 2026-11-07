using System.Threading.Channels;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

// M4 additions to the bridge: right button = long press, wheel = scroll, UI commands, status snapshot.
public class MirrorBridgeMouseTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingSink _sink;
    private readonly FakeDesktop _desktop = new();
    private readonly FakeVideo _video = new();
    private readonly Channel<MouseInputEvent> _input = Channel.CreateUnbounded<MouseInputEvent>();
    private readonly List<string> _log = [];
    private MirrorBridge _bridge;

    public MirrorBridgeMouseTests()
    {
        _sink = new RecordingSink(_time);
        _bridge = CreateBridge(new BridgeOptions { FollowVideoSize = false });
    }

    // Client 400x600 at (300,150), picture 498x1080 letterboxed: 276.67 x 600 centered horizontally.
    private static RectD Portrait => Geometry.VideoRect(400, 600, new SizeD(498, 1080)).Offset(300, 150);

    private static RectD LandscapePicture => Geometry.VideoRect(400, 600, new SizeD(1920, 886)).Offset(300, 150);

    private MirrorBridge CreateBridge(BridgeOptions options) =>
        new(new TouchGestureEngine(_sink, _time), _video, _desktop, _input.Reader, options, _time, _log.Add);

    private static PointD At(RectD r, double u, double v) => new(r.Left + (r.Width * u), r.Top + (r.Height * v));

    private Task Tick() => _time.Drive(_bridge.TickAsync());

    private void Send(MouseInputEvent e)
    {
        _desktop.CursorPosition = e.Position;
        _input.Writer.TryWrite(e);
    }

    [Fact]
    public async Task ARightClickIsALongPressHeldAtLeast600Ms()
    {
        var p = At(Portrait, 0.5, 0.5);
        Send(MouseInputEvent.Press(MouseButton.Right, p));
        Send(MouseInputEvent.Release(MouseButton.Right, p));

        await Tick();

        Assert.Equal(3, _sink.Frames.Count);
        Assert.True(_sink.Frames[0].IsDown);
        Assert.True(_sink.Frames[1].IsLift);
        Assert.InRange(_sink.Frames[1].At.TotalMilliseconds, 600, 605);
        Assert.Equal(1, _bridge.Engine.Stats.LongPresses);
        Assert.Equal(1, _bridge.Engine.Stats.Gestures);
    }

    [Fact]
    public async Task ARightDragMovesTheFingerAfterTheLongPress()
    {
        var pic = Portrait;
        Send(MouseInputEvent.Press(MouseButton.Right, At(pic, 0.5, 0.5)));
        await Tick();
        _time.Wait(700);
        _desktop.CursorPosition = At(pic, 0.5, 0.25);
        await Tick();
        Send(MouseInputEvent.Release(MouseButton.Right, At(pic, 0.5, 0.25)));
        await Tick();

        var downs = _sink.Downs.ToList();
        Assert.Equal(new AxisPoint(5000, 5000), downs[0].First);
        Assert.Equal(new AxisPoint(5000, 2500), downs[^1].First);
        Assert.True(_sink.Frames[^2].IsLift);
    }

    [Fact]
    public async Task CtrlDoesNotTurnARightPressIntoAPinch()
    {
        var p = At(Portrait, 0.3, 0.3);
        Send(MouseInputEvent.Press(MouseButton.Right, p, control: true));
        Send(MouseInputEvent.Release(MouseButton.Right, p));

        await Tick();

        Assert.Single(_sink.Frames[0].Contacts);
    }

    [Fact]
    public async Task TheOtherButtonsReleaseDoesNotLiftTheFinger()
    {
        var pic = Portrait;
        Send(MouseInputEvent.Press(MouseButton.Left, At(pic, 0.5, 0.5)));
        Send(MouseInputEvent.Press(MouseButton.Right, At(pic, 0.5, 0.5))); // ignored: already touching
        Send(MouseInputEvent.Release(MouseButton.Right, At(pic, 0.5, 0.5)));
        await Tick();

        Assert.True(_bridge.Engine.IsDown);
        Assert.False(_bridge.Engine.IsLongPress);

        Send(MouseInputEvent.Release(MouseButton.Left, At(pic, 0.5, 0.5)));
        await Tick();

        Assert.False(_bridge.Engine.IsDown);
        Assert.InRange(_sink.Frames[1].At.TotalMilliseconds, 70, 75); // the normal hold, not the long one
    }

    [Fact]
    public async Task RightButtonCanBeTurnedOff()
    {
        _bridge = CreateBridge(new BridgeOptions { FollowVideoSize = false, RightButtonLongPress = false });
        var p = At(Portrait, 0.5, 0.5);
        Send(MouseInputEvent.Press(MouseButton.Right, p));
        Send(MouseInputEvent.Release(MouseButton.Right, p));

        await Tick();

        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task WheelOutsideThePictureOrOverAnotherWindowIsIgnored()
    {
        Send(MouseInputEvent.Wheel(new PointD(305, 450), -120)); // letterbox bar
        await Tick();
        _desktop.WindowAt = _ => FakeDesktop.OtherWindow;
        Send(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120));
        await Tick();

        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task WheelWhileNotSubscribedIsCountedAsDropped()
    {
        _sink.IsSubscribed = false;

        Send(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120));
        await Tick();

        Assert.Empty(_sink.Frames);
        Assert.Equal(1, _bridge.Engine.Stats.Dropped);
    }

    [Fact]
    public async Task WheelCanBeTurnedOff()
    {
        _bridge = CreateBridge(new BridgeOptions { FollowVideoSize = false, WheelScroll = false });

        Send(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120));
        await Tick();

        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task TheHomeCommandWorksWithoutFocus()
    {
        _desktop.Foreground = 0;

        _bridge.Post(new BridgeCommand(BridgeCommandKind.Home));
        await Tick();

        Assert.Equal(new AxisPoint(5000, 9950), _sink.Downs.First().First);
        Assert.Contains(_log, l => l.Contains("home swipe sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheHomeCommandWorksWithoutAWindowUsingTheReportedOrientation()
    {
        _video.Window = 0;
        _video.VideoSize = new SizeD(1920, 886);

        _bridge.Post(new BridgeCommand(BridgeCommandKind.Home));
        await Tick();

        Assert.Equal(new AxisPoint(50, 5000), _sink.Downs.First().First); // rotated by 90
    }

    [Fact]
    public async Task RotationCommandsChangeTheRotationOnTheLoop()
    {
        _video.VideoSize = new SizeD(1920, 886); // landscape: the toggle switches the landscape rotation
        var changes = new List<Rotation>();
        _bridge.LandscapeRotationChanged += (_, r) => changes.Add(r);

        _bridge.Post(new BridgeCommand(BridgeCommandKind.ToggleRotation));
        Assert.Equal(Rotation.R90, _bridge.Engine.LandscapeRotation); // not before the next tick
        await Tick();
        _bridge.Post(new BridgeCommand(BridgeCommandKind.SetRotation, Rotation.R180));
        await Tick();

        Assert.Equal([Rotation.R270, Rotation.R180], changes);
        Assert.Equal(Rotation.R180, _bridge.Status.LandscapeRotation);
    }

    [Fact]
    public async Task TheStatusSnapshotDescribesTheLastTick()
    {
        Assert.Same(BridgeStatus.Initial, _bridge.Status);

        Send(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120));
        await Tick();

        var s = _bridge.Status;
        Assert.Equal(FakeDesktop.MirrorWindow, s.Window);
        Assert.Equal(Portrait, s.Picture);
        Assert.Equal(new SizeD(498, 1080), s.VideoSize);
        Assert.False(s.IsLandscape);
        Assert.Equal(1, s.Scrolls);

        _video.Window = 0;
        _video.VideoSize = new SizeD(1920, 886);
        await Tick();

        Assert.Null(_bridge.Status.Picture);
        Assert.True(_bridge.Status.IsLandscape);
    }
}
