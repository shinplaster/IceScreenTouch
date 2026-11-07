using System.Collections.Concurrent;
using System.Drawing;
using System.Threading.Channels;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using Xunit.Abstractions;

namespace IceScreenTouch.Windows.Tests;

/// <summary>The stand-in window, the real low-level hooks and the 1 ms timer, shared by the input tests.</summary>
public sealed class InputFixture : IDisposable
{
    private readonly IDisposable _timer = ProcessSetup.HighResolutionTimer();
    private readonly MouseHook _hook;
    private readonly KeyboardHook _keyboardHook;
    private Channel<MouseInputEvent> _buttons = Channel.CreateUnbounded<MouseInputEvent>();

    public InputFixture()
    {
        Saved = SyntheticInput.CursorPosition;
        Window = StandInWindow.Show(new Rectangle(300, 150, 400, 600));
        _hook = new MouseHook(e => Volatile.Read(ref _buttons).Writer.TryWrite(e));
        _hook.Start();

        // Keys typed while the stand-in has the focus are recorded instead of sent.
        var desktop = new Win32Desktop();
        Keyboard = new KeyboardForwarder(() => desktop.IsForeground(Window.Handle), KeyboardHook.ToChar, Keys.Enqueue);
        _keyboardHook = new KeyboardHook(Keyboard);
        _keyboardHook.Start();
    }

    internal StandInWindow Window { get; }

    /// <summary>The forwarder behind the real keyboard hook.</summary>
    internal KeyboardForwarder Keyboard { get; }

    /// <summary>What the forwarder sent (clear it before a test step).</summary>
    internal ConcurrentQueue<KeyboardInputEvent> Keys { get; } = new();

    internal (int X, int Y) Saved { get; }

    /// <summary>A fresh channel for the next bridge, so events of an earlier test cannot leak into it.</summary>
    internal ChannelReader<MouseInputEvent> NewChannel()
    {
        var c = Channel.CreateUnbounded<MouseInputEvent>();
        Volatile.Write(ref _buttons, c);
        return c.Reader;
    }

    public void Dispose()
    {
        SyntheticInput.LeftButton(false);
        SyntheticInput.RightButton(false);
        SyntheticInput.Key(SyntheticInput.Control, false);
        SyntheticInput.Key(SyntheticInput.Shift, false);
        SyntheticInput.MoveTo(Saved.X, Saved.Y);
        _keyboardHook.Dispose();
        _hook.Dispose();
        Window.Dispose();
        _timer.Dispose();
    }
}

/// <summary>
/// End-to-end check of the bridge with real Win32 input, ported from poc/poc3-integrated/test_bridge.py. A WinForms
/// window plays the UxPlay window. Clicks and keys go through SendInput and the real low-level hook; the cursor
/// is moved with SetCursorPos, which the hook never sees (like the bridge expects). Expected coordinates come
/// from WinForms' geometry, independent of the Win32 calls the bridge uses. Moves the real cursor for a while.
/// </summary>
public sealed class BridgeInputTests(InputFixture fixture, ITestOutputHelper output) : IClassFixture<InputFixture>
{
    private static readonly SizeD Portrait = new(498, 1080);
    private static readonly SizeD Landscape = new(1920, 886);
    private static readonly string[] Tap = ["down", "lift", "empty"];

    private StandInWindow Window => fixture.Window;

    /// <summary>Axis value of a screen pixel, from WinForms' client rectangle and the letterbox rule.</summary>
    private AxisPoint ExpectedAxis(int px, int py, SizeD video)
    {
        var c = Window.ClientOnScreen;
        var scale = Math.Min(c.Width / video.Width, c.Height / video.Height);
        var (vw, vh) = (video.Width * scale, video.Height * scale);
        var (vl, vt) = (c.X + ((c.Width - vw) / 2), c.Y + ((c.Height - vh) / 2));
        static ushort Clamp(double v) => (ushort)Math.Clamp(Math.Round(v), 0, HidConstants.AxisMax);
        return new AxisPoint(Clamp((px - vl) / vw * HidConstants.AxisMax), Clamp((py - vt) / vh * HidConstants.AxisMax));
    }

    private static void Near(AxisPoint expected, AxisPoint actual, int tolerance = 2)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance, $"expected {expected}, got {actual}");
    }

    private Run StartBridge(RealTimeSink sink, bool follow = false, SizeD? video = null)
    {
        Window.Invoke(() => Win32Windows.SetClientSize(Window.Handle, 400, 600));
        var source = new StandInVideo(Window.Handle) { VideoSize = video ?? Portrait };
        var bridge = new MirrorBridge(
            new TouchGestureEngine(sink),
            source,
            new Win32Desktop(fixture.Keyboard.IsDown),
            fixture.NewChannel(),
            new BridgeOptions { FollowVideoSize = follow },
            log: output.WriteLine);
        return new Run(bridge, source);
    }

    private static async Task Drag((int X, int Y)[] path, bool ctrl = false, int holdMs = 40, int stepMs = 30)
    {
        SyntheticInput.MoveTo(path[0].X, path[0].Y);
        await Task.Delay(50);
        if (ctrl)
        {
            SyntheticInput.Key(SyntheticInput.Control, true);
            await Task.Delay(30);
        }

        SyntheticInput.LeftButton(true);
        await Task.Delay(holdMs);
        foreach (var (x, y) in path.Skip(1))
        {
            SyntheticInput.MoveTo(x, y);
            await Task.Delay(stepMs);
        }

        SyntheticInput.LeftButton(false);
        await Task.Delay(250); // the bridge finishes the minimum hold and the lift
        if (ctrl)
        {
            SyntheticInput.Key(SyntheticInput.Control, false);
            await Task.Delay(30);
        }
    }

    private static async Task PressKey(int vk, int modifier = 0)
    {
        if (modifier != 0)
        {
            SyntheticInput.Key(modifier, true);
            await Task.Delay(30);
        }

        SyntheticInput.Key(vk, true);
        await Task.Delay(60);
        SyntheticInput.Key(vk, false);
        await Task.Delay(30);
        if (modifier != 0)
        {
            SyntheticInput.Key(modifier, false);
        }

        await Task.Delay(100);
    }

    /// <summary>Clicks the letterbox bar (ignored by the bridge) so that the stand-in gets the keyboard focus.</summary>
    private async Task FocusStandIn()
    {
        var c = Window.ClientOnScreen;
        await Drag([(c.X + 3, c.Y + (c.Height / 2))]);
        Window.Activate();
        await Task.Delay(150);
        Assert.True(new Win32Desktop().IsForeground(Window.Handle), "the stand-in window could not take the keyboard focus");
    }

    private static (int X, int Y) At(RectD r, double u, double v) => ((int)Math.Round(r.Left + (r.Width * u)), (int)Math.Round(r.Top + (r.Height * v)));

    [InputFact]
    public async Task ClicksAndDragsMapToTheRightPoints()
    {
        var sink = new RealTimeSink();
        await using var run = StartBridge(sink);
        var pic = await run.PictureAsync();
        var center = At(pic, 0.5, 0.5);

        // 1. tap in the middle of the picture
        await Drag([center]);
        Assert.Equal(Tap, sink.Frames.Select(f => f.Kind));
        Near(new AxisPoint(5000, 5000), sink.Frames[0].First, 60);
        Near(ExpectedAxis(center.X, center.Y, Portrait), sink.Frames[0].First);

        // 2. drag from 25% to 75% of the picture
        sink.Clear();
        var path = Enumerable.Range(0, 9).Select(i => At(pic, 0.25 + (0.5 * i / 8), 0.25 + (0.5 * i / 8))).ToArray();
        await Drag(path);
        var downs = sink.Downs.Select(f => f.First).ToList();
        Assert.True(downs.Count >= 6, $"{downs.Count} frames");
        Assert.All(downs.Zip(downs.Skip(1)), p => Assert.True(p.Second.X >= p.First.X && p.Second.Y >= p.First.Y));
        Near(new AxisPoint(2500, 2500), downs[0], 60);
        Near(new AxisPoint(7500, 7500), downs[^1], 60);
        Assert.Equal("empty", sink.Frames[^1].Kind);

        // 3. click in the letterbox bar (inside the window, outside the picture)
        sink.Clear();
        var bar = (Window.ClientOnScreen.X + 5, center.Y);
        await Drag([bar]);
        Assert.Empty(sink.Frames);

        // 4. press outside the picture and drag in: no touch
        await Drag([bar, center]);
        Assert.Empty(sink.Frames);

        // 5. drag out of the picture: clamps to the edge, still lifts
        await Drag([center, ((int)(pic.Left + pic.Width + 40), center.Y)]);
        Assert.Equal(HidConstants.AxisMax, sink.Downs[^1].First.X);
        Assert.Equal("empty", sink.Frames[^1].Kind);

        // 6. Ctrl + drag = pinch with a mirrored second finger
        sink.Clear();
        await Drag([At(pic, 0.6, 0.5), At(pic, 0.8, 0.5)], ctrl: true);
        Assert.NotEmpty(sink.Downs);
        Assert.All(sink.Downs, f => Assert.True(
            f.Contacts.Length == 2 && f.Contacts[0].X + f.Contacts[1].X == HidConstants.AxisMax && f.Contacts[0].Y + f.Contacts[1].Y == HidConstants.AxisMax,
            string.Join(" ", f.Contacts)));
    }

    [InputFact]
    public async Task APressWhereAnotherWindowCoversThePictureIsIgnored()
    {
        var sink = new RealTimeSink();
        await using var run = StartBridge(sink);
        var pic = await run.PictureAsync();
        var (x, y) = At(pic, 0.5, 0.5);
        using var cover = StandInWindow.Show(new Rectangle(x - 40, y - 40, 80, 80), color: Color.DarkRed);

        await Drag([(x, y)]);

        Assert.Empty(sink.Frames);
    }

    [InputFact]
    public async Task FastClicksOnASlowLinkAreNeverLost()
    {
        // The PoC 3 symptom: 8 very short clicks while each BLE send takes 60 ms must all arrive.
        var sink = new RealTimeSink { SendDelay = TimeSpan.FromMilliseconds(60) };
        await using var run = StartBridge(sink);
        var center = At(await run.PictureAsync(), 0.5, 0.5);
        SyntheticInput.MoveTo(center.X, center.Y);
        await Task.Delay(50);

        for (var i = 0; i < 8; i++)
        {
            SyntheticInput.LeftButton(true);
            await Task.Delay(12);
            SyntheticInput.LeftButton(false);
        SyntheticInput.RightButton(false);
            await Task.Delay(25);
        }

        await run.WaitUntil(() => sink.Frames.Count >= 24, TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Repeat(Tap, 8).SelectMany(t => t), sink.Frames.Select(f => f.Kind));
        var holds = sink.Frames.Where(f => f.IsDown).Zip(sink.Frames.Where(f => f.IsLift), (d, l) => l.At - d.At).ToList();
        Assert.All(holds, h => Assert.True(h >= TimeSpan.FromMilliseconds(64), $"held {h.TotalMilliseconds:0} ms"));
        output.WriteLine($"holds: {string.Join(", ", holds.Select(h => $"{h.TotalMilliseconds:0}"))} ms");
    }

    [InputFact]
    public async Task AFastSweepIsRateLimitedAndEndsAtTheFinalPosition()
    {
        var sink = new RealTimeSink();
        await using var run = StartBridge(sink);
        var pic = await run.PictureAsync();
        var start = At(pic, 0.2, 0.5);
        SyntheticInput.MoveTo(start.X, start.Y);
        await Task.Delay(50);
        SyntheticInput.LeftButton(true);
        await Task.Delay(30);

        var sweep = System.Diagnostics.Stopwatch.StartNew();
        (int X, int Y) last = start;
        for (var i = 1; i <= 60; i++)
        {
            last = At(pic, 0.2 + (0.6 * i / 60), 0.5);
            SyntheticInput.MoveTo(last.X, last.Y);
            await Task.Delay(2);
        }

        var elapsed = sweep.Elapsed;
        SyntheticInput.LeftButton(false);
        SyntheticInput.RightButton(false);
        await Task.Delay(300);

        var downs = sink.Downs;
        var limit = (int)(elapsed.TotalMilliseconds / 16) + 4;
        output.WriteLine($"{downs.Count} frames in {elapsed.TotalMilliseconds:0} ms (limit {limit})");
        Assert.InRange(downs.Count, 2, limit);
        Near(ExpectedAxis(last.X, last.Y, Portrait), downs[^1].First);
        Assert.Equal("empty", sink.Frames[^1].Kind);
    }

    [InputFact]
    public async Task TheWindowFollowsRotationAndLandscapeClicksAreRotated()
    {
        var sink = new RealTimeSink();
        await using var run = StartBridge(sink, follow: true);
        var frame = Window.Frame;
        var work = Window.WorkingArea;

        static (int W, int H) Fit(SizeD v, Rectangle work, Size frame) =>
            Geometry.FitClientSize(v, work.Width, work.Height, frame.Width, frame.Height);

        // portrait -> landscape -> portrait
        run.Source.VideoSize = Landscape;
        await run.WaitUntil(() => Window.ClientOnScreen.Width > Window.ClientOnScreen.Height, TimeSpan.FromSeconds(2));
        var fit = Fit(Landscape, work, frame);
        Assert.Equal(new Size(fit.W, fit.H), Window.ClientOnScreen.Size);

        run.Source.VideoSize = Portrait;
        await run.WaitUntil(() => Window.ClientOnScreen.Height > Window.ClientOnScreen.Width, TimeSpan.FromSeconds(2));
        fit = Fit(Portrait, work, frame);
        Assert.Equal(new Size(fit.W, fit.H), Window.ClientOnScreen.Size);
        var p = await run.PictureAsync();
        Assert.True(Math.Abs(p.Width - fit.W) <= 1 && Math.Abs(p.Height - fit.H) <= 1, $"letterbox left: {p}");

        // landscape: each rotation sends the hand-computed point
        run.Source.VideoSize = Landscape;
        await run.WaitUntil(() => Window.ClientOnScreen.Width > Window.ClientOnScreen.Height, TimeSpan.FromSeconds(2));
        var pic = await run.PictureAsync();
        var click = At(pic, 0.75, 0.25);
        var e = ExpectedAxis(click.X, click.Y, Landscape);
        var (u, v) = (e.X / (double)HidConstants.AxisMax, e.Y / (double)HidConstants.AxisMax);
        var hand = new Dictionary<Rotation, (double X, double Y)>
        {
            [Rotation.R0] = (u, v),
            [Rotation.R90] = (1 - v, u),
            [Rotation.R180] = (1 - u, 1 - v),
            [Rotation.R270] = (v, 1 - u),
        };
        foreach (var (rotation, (hx, hy)) in hand)
        {
            run.Bridge.Engine.LandscapeRotation = rotation;
            sink.Clear();
            await Drag([click]);
            Near(new AxisPoint((ushort)Math.Round(hx * HidConstants.AxisMax), (ushort)Math.Round(hy * HidConstants.AxisMax)), sink.Frames[0].First);
        }

        // a portrait picture is never rotated, whatever the landscape setting is
        run.Source.VideoSize = Portrait;
        await run.WaitUntil(() => Window.ClientOnScreen.Height > Window.ClientOnScreen.Width, TimeSpan.FromSeconds(2));
        run.Bridge.Engine.LandscapeRotation = Rotation.R90;
        var pp = At(await run.PictureAsync(), 0.25, 0.75);
        sink.Clear();
        await Drag([pp]);
        Near(ExpectedAxis(pp.X, pp.Y, Portrait), sink.Frames[0].First);
    }

    [InputFact]
    public async Task HomeAndRotateKeysWorkWhileTheWindowHasTheFocus()
    {
        var sink = new RealTimeSink();
        await using var run = StartBridge(sink);
        await run.PictureAsync();
        await FocusStandIn();

        // Home = home swipe from the bottom edge of the portrait picture (this sink has no home button)
        sink.Clear();
        await PressKey(SyntheticInput.Home);
        await run.WaitUntil(() => sink.Frames.Any(f => f.IsEmpty), TimeSpan.FromSeconds(2));
        Assert.Equal(new AxisPoint(5000, 9950), sink.Downs[0].First);
        Assert.Equal(new AxisPoint(5000, 6500), sink.Downs[^1].First);

        // landscape: Ctrl+R toggles 90 <-> 270 (R alone does not); Home swipes along the rotated bottom edge
        run.Source.VideoSize = Landscape;
        await Task.Delay(100);
        await PressKey(SyntheticInput.R, SyntheticInput.Control);
        Assert.Equal(Rotation.R270, run.Bridge.Engine.LandscapeRotation);
        await PressKey(SyntheticInput.R);
        Assert.Equal(Rotation.R270, run.Bridge.Engine.LandscapeRotation);
        await PressKey(SyntheticInput.R, SyntheticInput.Control);
        Assert.Equal(Rotation.R90, run.Bridge.Engine.LandscapeRotation);
        sink.Clear();
        await PressKey(SyntheticInput.Home);
        await run.WaitUntil(() => sink.Frames.Any(f => f.IsEmpty), TimeSpan.FromSeconds(2));
        Assert.Equal(new AxisPoint(50, 5000), sink.Downs[0].First);
        Assert.Equal(new AxisPoint(3500, 5000), sink.Downs[^1].First);
    }

    [InputFact]
    public async Task KeysGoToTheDeviceOnlyWhileTheWindowHasTheFocus()
    {
        var sink = new RealTimeSink();
        await using var run = StartBridge(sink);
        await run.PictureAsync();
        await FocusStandIn();
        fixture.Keyboard.DeviceLayout = HidKeyboardLayout.Us;

        // a, Shift+A, Ctrl+A (= Command+A), Ctrl+Space (stays Ctrl+Space), Enter; the shortcuts are not sent
        fixture.Keys.Clear();
        await PressKey(SyntheticInput.A);
        await PressKey(SyntheticInput.A, SyntheticInput.Shift);
        await PressKey(SyntheticInput.A, SyntheticInput.Control);
        await PressKey(0x20, SyntheticInput.Control);
        await PressKey(0x0D);
        await PressKey(SyntheticInput.R, SyntheticInput.Control);
        await PressKey(SyntheticInput.Home);
        await run.WaitUntil(() => sink.Frames.Any(f => f.IsEmpty), TimeSpan.FromSeconds(2)); // the Home swipe ended
        var downs = fixture.Keys.Where(k => k.Down).Select(k => k.Key).ToArray();
        Assert.Equal(
            [
                new HidKey(0x04),
                new HidKey(0x04, HidModifiers.LeftShift),
                new HidKey(0x04, HidModifiers.LeftGui),
                new HidKey(HidKeyboard.Space, HidModifiers.LeftControl),
                new HidKey(HidKeyboard.Enter),
            ],
            downs);
        Assert.Equal(downs.Length, fixture.Keys.Count(k => !k.Down)); // every press was released

        // the relay turns them into reports that end with everything released
        var relay = new KeyboardRelay();
        foreach (var e in fixture.Keys)
        {
            relay.Apply(e);
        }

        Assert.Equal(KeyboardReport.Released, relay.Last);

        // without the focus, keys stay with Windows
        var other = StandInWindow.Show(new Rectangle(800, 150, 200, 200));
        try
        {
            other.Activate();
            await Task.Delay(150);
            fixture.Keys.Clear();
            await PressKey(SyntheticInput.A);
            Assert.Empty(fixture.Keys);
        }
        finally
        {
            other.Dispose();
        }
    }

    [InputFact]
    public async Task RightClickIsALongPressAndTheWheelScrolls()
    {
        var sink = new RealTimeSink();
        await using var run = StartBridge(sink);
        var pic = await run.PictureAsync();
        var center = At(pic, 0.5, 0.5);

        // right click: one finger, held at least 600 ms even though the button was up after 40 ms
        SyntheticInput.MoveTo(center.X, center.Y);
        await Task.Delay(50);
        SyntheticInput.RightButton(true);
        await Task.Delay(40);
        SyntheticInput.RightButton(false);
        await run.WaitUntil(() => sink.Frames.Any(f => f.IsEmpty), TimeSpan.FromSeconds(2));
        Assert.Equal(Tap, sink.Frames.Select(f => f.Kind));
        var held = sink.Frames[1].At - sink.Frames[0].At;
        output.WriteLine($"long press held {held.TotalMilliseconds:0} ms");
        Assert.InRange(held.TotalMilliseconds, 590, 800);
        Near(ExpectedAxis(center.X, center.Y, Portrait), sink.Frames[0].First);

        // wheel down one notch: the finger follows up by 10% of the picture and (no momentum) rests before lifting
        run.Bridge.Engine.Wheel = WheelOptions.Default with { Momentum = 0, StepPerNotch = 0.10 };
        sink.Clear();
        SyntheticInput.Wheel(-120);
        await run.WaitUntil(() => sink.Frames.Any(f => f.IsEmpty), TimeSpan.FromSeconds(2));
        var downs = sink.Downs.Select(f => f.First).ToList();
        Near(ExpectedAxis(center.X, center.Y, Portrait), downs[0]);
        Assert.InRange(downs[0].Y - downs[^1].Y, 990, 1010);
        Assert.Equal(downs[0].X, downs[^1].X);

        // Shift + wheel down = scroll right = the finger moves left
        sink.Clear();
        SyntheticInput.Key(SyntheticInput.Shift, true);
        await Task.Delay(30);
        SyntheticInput.Wheel(-120);
        await Task.Delay(30);
        SyntheticInput.Key(SyntheticInput.Shift, false);
        await run.WaitUntil(() => sink.Frames.Any(f => f.IsEmpty), TimeSpan.FromSeconds(2));
        downs = sink.Downs.Select(f => f.First).ToList();
        Assert.InRange(downs[0].X - downs[^1].X, 990, 1010);

        // the wheel in the letterbox bar does nothing
        sink.Clear();
        SyntheticInput.MoveTo(Window.ClientOnScreen.X + 5, center.Y);
        await Task.Delay(50);
        SyntheticInput.Wheel(-120);
        await Task.Delay(300);
        Assert.Empty(sink.Frames);
    }

    /// <summary>A bridge running on the real clock until disposed.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;

        public Run(MirrorBridge bridge, StandInVideo source)
        {
            Bridge = bridge;
            Source = source;
            _task = Task.Run(() => bridge.RunAsync(_cts.Token));
        }

        public MirrorBridge Bridge { get; }

        public StandInVideo Source { get; }

        /// <summary>Waits until the bridge has measured the picture and returns it.</summary>
        public async Task<RectD> PictureAsync()
        {
            await WaitUntil(() => Bridge.Picture is not null, TimeSpan.FromSeconds(2));
            await Task.Delay(50);
            return Bridge.Picture!.Value;
        }

        public async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (_task.IsFaulted)
                {
                    await _task;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("condition not reached");
                }

                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task;
            _cts.Dispose();
        }
    }
}
