// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

// The user has priority over the external API (docs/API-DESIGN.md, 9.2): while an ApiInputLease is held the bridge and the
// keyboard relay send nothing, the first user input interrupts the API and is replayed once the lease is disposed.
public class ApiInputArbiterTests : IAsyncDisposable
{
    private static readonly string[] TapFrames = ["down", "lift", "empty"];

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingSink _sink;
    private readonly FakeDesktop _desktop = new();
    private readonly FakeVideo _video = new();
    private readonly Channel<MouseInputEvent> _mouse = Channel.CreateUnbounded<MouseInputEvent>();
    private readonly Channel<KeyboardInputEvent> _keys = Channel.CreateUnbounded<KeyboardInputEvent>();
    private readonly RecordingKeys _keySink = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _log = [];
    private readonly ApiInputArbiter _arbiter;
    private readonly MirrorBridge _bridge;
    private readonly List<Rotation> _rotations = [];
    private Task _relay = Task.CompletedTask;

    public ApiInputArbiterTests()
    {
        _sink = new RecordingSink(_time);
        _arbiter = new ApiInputArbiter(_time);
        _bridge = new MirrorBridge(
            new TouchGestureEngine(_sink, _time),
            _video,
            _desktop,
            _mouse.Reader,
            new BridgeOptions { FollowVideoSize = false },
            _time,
            _log.Add,
            _arbiter);
        _bridge.LandscapeRotationChanged += (_, r) => _rotations.Add(r);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _relay;
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    // Client 400x600 at (300,150), picture 498x1080 letterboxed: 276.67 x 600 centered horizontally.
    private static RectD Portrait => Geometry.VideoRect(400, 600, new SizeD(498, 1080)).Offset(300, 150);

    private static PointD At(RectD r, double u, double v) => new(r.Left + (r.Width * u), r.Top + (r.Height * v));

    private static string Kind(Frame f) => f.IsEmpty ? "empty" : f.IsDown ? "down" : "lift";

    private static KeyboardInputEvent Down(int code, byte usage, byte modifiers = 0, KeyRole role = KeyRole.Character) =>
        new(code, true, new HidKey(usage, modifiers), role);

    private static KeyboardInputEvent Up(int code) => new(code, false, default, KeyRole.Character);

    private Task Tick() => _time.Drive(_bridge.TickAsync());

    private void Press(PointD p, MouseButton button = MouseButton.Left)
    {
        _desktop.CursorPosition = p;
        _mouse.Writer.TryWrite(MouseInputEvent.Press(button, p));
    }

    private void Release(PointD p, MouseButton button = MouseButton.Left)
    {
        _desktop.CursorPosition = p;
        _mouse.Writer.TryWrite(MouseInputEvent.Release(button, p));
    }

    private async Task<ApiInputLease> AcquireAsync()
    {
        var pending = _arbiter.TryAcquireAsync().AsTask();
        Assert.False(pending.IsCompleted); // the bridge answers on its next tick
        await Tick();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Acquired, $"refused: {result.Refusal}");
        return result.Lease!;
    }

    private async Task<ApiInputRefusal> RefusedAsync()
    {
        var pending = _arbiter.TryAcquireAsync().AsTask();
        if (!pending.IsCompleted)
        {
            await Tick();
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Acquired);
        return result.Refusal;
    }

    private void StartRelay() =>
        _relay = Task.Run(() => new KeyboardRelay().RunAsync(_keys.Reader, _keySink, arbiter: _arbiter, cancellationToken: _stop.Token));

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(1);
        }
    }

    // ---- acquisition -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBridgeAnswersAnAcquisitionOnItsNextTick()
    {
        var pending = _arbiter.TryAcquireAsync().AsTask();

        Assert.False(pending.IsCompleted);
        Assert.True(_arbiter.HoldsInput);

        await Tick();
        var result = await pending;

        Assert.True(result.Acquired);
        Assert.Equal(ApiInputArbiter.ApiPhase.Held, _arbiter.Phase);
        Assert.False(result.Lease!.UserInput.IsCancellationRequested);
    }

    [Fact]
    public async Task OnlyOneLeaseAtATime()
    {
        await using var lease = await AcquireAsync();

        var second = await _arbiter.TryAcquireAsync();

        Assert.Equal(ApiInputRefusal.Busy, second.Refusal);
    }

    [Fact]
    public async Task AcquisitionIsRefusedWhileTheUsersFingerIsDown()
    {
        Press(At(Portrait, 0.5, 0.5));
        await Tick();
        Assert.True(_bridge.Engine.IsDown);
        Assert.True(_arbiter.UserInputActive);

        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());
    }

    [Fact]
    public async Task AcquisitionIsRefusedWhileTheWheelScrolls()
    {
        _mouse.Writer.TryWrite(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120));
        await Tick();
        Assert.True(_bridge.Engine.IsScrolling);
        Assert.True(_arbiter.UserInputActive);

        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());
    }

    [Fact]
    public async Task AcquisitionIsRefusedWhileAPressIsStillInTheChannelAndThePressGoesThrough()
    {
        Press(At(Portrait, 0.5, 0.5));

        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());

        // The refused tick processed the press normally.
        Assert.True(_bridge.Engine.IsDown);
        Assert.Single(_sink.Downs);
        Assert.False(_arbiter.HoldsInput);
    }

    [Fact]
    public async Task AcquisitionIsRefusedWhileAShortcutOrACommandIsPending()
    {
        _desktop.CountsPresses = true;
        _desktop.Presses[ShortcutKey.Home] = 1;
        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());
        Assert.NotEmpty(_sink.Frames); // the Home press was not lost

        _bridge.Post(new BridgeCommand(BridgeCommandKind.ToggleRotation));
        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());
        Assert.Equal(Rotation.R180, _bridge.Engine.PortraitRotation);
    }

    [Fact]
    public async Task APressThatIsNotATouchDoesNotRefuseAcquisition()
    {
        Press(new PointD(305, 450)); // letterbox bar
        _mouse.Writer.TryWrite(MouseInputEvent.Press(MouseButton.Left, At(Portrait, 0.5, 0.5), alt: true));
        Release(At(Portrait, 0.5, 0.5));

        await using var lease = await AcquireAsync();

        Assert.False(lease.UserInput.IsCancellationRequested);
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task AcquisitionIsRefusedWhileKeysAreHeld()
    {
        StartRelay();
        _keys.Writer.TryWrite(Down(65, 0x04));
        await Eventually(() => _keySink.Reports.Count == 1, "the key report");
        await Eventually(() => _arbiter.UserInputActive, "the held key");

        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());

        _keys.Writer.TryWrite(Up(65));
        await Eventually(() => _keySink.Reports.Count == 2, "the release");
        await Eventually(() => !_arbiter.UserInputActive, "the keys to be released");
        await using var lease = await AcquireAsync();
        Assert.True(_arbiter.HoldsInput);
    }

    [Fact]
    public async Task AcquisitionCanBeCancelledAndThenNothingIsHeld()
    {
        using var cts = new CancellationTokenSource();
        var pending = _arbiter.TryAcquireAsync(cts.Token).AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(_arbiter.HoldsInput);
        await using var lease = await AcquireAsync();
    }

    [Fact]
    public async Task AnAcquisitionAlreadyCancelledNeverStarts()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _arbiter.TryAcquireAsync(cts.Token).AsTask());

        Assert.False(_arbiter.HoldsInput);
    }

    // ---- the bridge in API mode ------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingReachesTheDeviceWhileTheLeaseIsHeldAndOtherMouseMovesDoNotInterrupt()
    {
        await using var lease = await AcquireAsync();

        _desktop.CursorPosition = At(Portrait, 0.2, 0.2);
        await Tick();
        Press(new PointD(305, 450)); // outside the picture
        _mouse.Writer.TryWrite(MouseInputEvent.Press(MouseButton.Left, At(Portrait, 0.5, 0.5), alt: true)); // for the receiver
        Release(At(Portrait, 0.5, 0.5));
        await Tick();
        _desktop.WindowAt = _ => FakeDesktop.OtherWindow; // covered by another window
        Press(At(Portrait, 0.5, 0.5));
        _mouse.Writer.TryWrite(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120));
        await Tick();

        Assert.False(lease.UserInput.IsCancellationRequested);
        Assert.Empty(_sink.Frames);
        Assert.Null(_arbiter.LastUserInput);

        // Nothing was held: giving the input back replays nothing.
        _desktop.WindowAt = _ => FakeDesktop.MirrorWindow;
        await lease.DisposeAsync();
        await Tick();
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task TheFirstPressFiresTheTokenIsHeldAndReplayedAsATouchAfterTheLeaseIsDisposed()
    {
        var p = At(Portrait, 0.5, 0.5);
        var lease = await AcquireAsync();

        Press(p);
        Release(p);
        await Tick();

        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.Empty(_sink.Frames);
        Assert.False(_bridge.Engine.IsDown);
        Assert.True(_arbiter.UserInputActive);

        await lease.DisposeAsync();
        await Tick();

        Assert.Equal(TapFrames, _sink.Frames.Select(Kind));
        Assert.Equal(new AxisPoint(5000, 5000), _sink.Frames[0].First);
        Assert.All(_sink.Frames.Where(f => !f.IsEmpty), f => Assert.NotEqual(new AxisPoint(0, 0), f.First));
        Assert.False(_arbiter.UserInputActive);
    }

    [Fact]
    public async Task PressesAndReleasesAreReplayedInOrderEvenWhenTheyCameInSeveralTicks()
    {
        var a = At(Portrait, 0.25, 0.25);
        var b = At(Portrait, 0.75, 0.75);
        var lease = await AcquireAsync();

        Press(a);
        await Tick();
        Release(a);
        await Tick();
        Press(b);
        Release(b);
        await Tick();
        await lease.DisposeAsync();
        await Tick();

        Assert.Equal(["down", "lift", "empty", "down", "lift", "empty"], _sink.Frames.Select(Kind));
        Assert.Equal(new AxisPoint(2500, 2500), _sink.Downs.First().First);
        Assert.Equal(new AxisPoint(7500, 7500), _sink.Downs.Last().First);
    }

    [Fact]
    public async Task APressStillHeldWhenTheLeaseIsGivenBackStartsFromThere()
    {
        var p = At(Portrait, 0.5, 0.5);
        var lease = await AcquireAsync();
        Press(p);
        await Tick();
        await lease.DisposeAsync();

        await Tick();

        Assert.True(_bridge.Engine.IsDown);
        Assert.Single(_sink.Downs);
        Release(p);
        await Tick();
        Assert.Equal(TapFrames, _sink.Frames.Select(Kind));
    }

    [Fact]
    public async Task ARightPressIsAUserInputToo()
    {
        var p = At(Portrait, 0.5, 0.5);
        var lease = await AcquireAsync();

        Press(p, MouseButton.Right);
        Release(p, MouseButton.Right);
        await Tick();
        Assert.True(lease.UserInput.IsCancellationRequested);
        await lease.DisposeAsync();
        await Tick();

        Assert.Equal(1, _bridge.Engine.Stats.LongPresses);
    }

    [Fact]
    public async Task TheWheelInterruptsAndScrollsAfterTheLeaseIsDisposed()
    {
        var lease = await AcquireAsync();

        _mouse.Writer.TryWrite(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120));
        await Tick();

        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.Empty(_sink.Frames);
        Assert.False(_bridge.Engine.IsScrolling);

        await lease.DisposeAsync();
        await Tick();

        Assert.True(_bridge.Engine.IsScrolling);
        Assert.NotEmpty(_sink.Frames);
    }

    [Fact]
    public async Task TheHomeShortcutInterruptsAndIsPerformedAfterTheLeaseIsDisposed()
    {
        _desktop.CountsPresses = true;
        var lease = await AcquireAsync();

        _desktop.Presses[ShortcutKey.Home] = 1;
        await Tick();

        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.Empty(_sink.Frames);

        await lease.DisposeAsync();
        await Tick();

        Assert.NotEmpty(_sink.Frames);
    }

    [Fact]
    public async Task TheRotateShortcutInterruptsAndIsPerformedAfterTheLeaseIsDisposed()
    {
        _desktop.CountsPresses = true;
        var lease = await AcquireAsync();

        _desktop.Presses[ShortcutKey.Rotate] = 1;
        await Tick();

        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.Equal(Rotation.R0, _bridge.Engine.PortraitRotation);

        await lease.DisposeAsync();
        await Tick();

        Assert.Equal(Rotation.R180, _bridge.Engine.PortraitRotation);
    }

    [Fact]
    public async Task ShortcutsWithoutTheFocusAreNotUserInput()
    {
        _desktop.CountsPresses = true;
        _desktop.Foreground = FakeDesktop.OtherWindow;
        await using var lease = await AcquireAsync();

        _desktop.Presses[ShortcutKey.Home] = 1;
        await Tick();

        Assert.False(lease.UserInput.IsCancellationRequested);
    }

    [Fact]
    public async Task TheUsersHomeAndRotationCommandsInterruptAndAreAppliedAfterTheLeaseIsDisposed()
    {
        var lease = await AcquireAsync();

        _bridge.Post(new BridgeCommand(BridgeCommandKind.Home));
        _bridge.Post(new BridgeCommand(BridgeCommandKind.ToggleRotation));
        await Tick();

        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.Empty(_sink.Frames);
        Assert.Equal(Rotation.R0, _bridge.Engine.PortraitRotation);

        await lease.DisposeAsync();
        await Tick();

        Assert.NotEmpty(_sink.Frames);
        Assert.Equal(Rotation.R180, _bridge.Engine.PortraitRotation);
    }

    [Fact]
    public async Task ACommandFromTheApiIsAppliedAtOnceWithoutInterrupting()
    {
        await using var lease = await AcquireAsync();

        _bridge.Post(new BridgeCommand(BridgeCommandKind.SetRotation, Rotation.R270, FromApi: true));
        await Tick();

        Assert.Equal(Rotation.R270, _bridge.Engine.LandscapeRotation);
        Assert.Equal([Rotation.R270], _rotations); // saved like Ctrl+R
        Assert.False(lease.UserInput.IsCancellationRequested);
        Assert.Null(_arbiter.LastUserInput);
    }

    [Fact]
    public async Task APortraitRotationCommandFromTheApiIsAppliedAtOnceAndNotSaved()
    {
        await using var lease = await AcquireAsync();

        _bridge.Post(new BridgeCommand(BridgeCommandKind.SetPortraitRotation, Rotation.R180, FromApi: true));
        await Tick();

        Assert.Equal(Rotation.R180, _bridge.Engine.PortraitRotation);
        Assert.Equal(Rotation.R180, _bridge.Status.PortraitRotation);
        Assert.Empty(_rotations); // the upside-down portrait is never saved
        Assert.False(lease.UserInput.IsCancellationRequested);
        Assert.Null(_arbiter.LastUserInput);

        // Only 0 and 180 are portrait rotations: anything else is ignored.
        _bridge.Post(new BridgeCommand(BridgeCommandKind.SetPortraitRotation, Rotation.R90, FromApi: true));
        await Tick();
        Assert.Equal(Rotation.R180, _bridge.Engine.PortraitRotation);
    }

    [Fact]
    public async Task ACommandFromTheApiDoesNotCountAsUserInputWithoutALeaseEither()
    {
        _bridge.Post(new BridgeCommand(BridgeCommandKind.SetRotation, Rotation.R270, FromApi: true));
        await Tick();

        Assert.Equal(Rotation.R270, _bridge.Engine.LandscapeRotation);
        Assert.Null(_arbiter.LastUserInput);

        _bridge.Post(new BridgeCommand(BridgeCommandKind.SetRotation, Rotation.R90));
        await Tick();
        Assert.NotNull(_arbiter.LastUserInput);
    }

    [Fact]
    public async Task WithoutAPictureThePendingClicksAreDroppedButTheUsersCommandsStillInterrupt()
    {
        var lease = await AcquireAsync();
        _video.Window = 0;

        Press(At(Portrait, 0.5, 0.5));
        Release(At(Portrait, 0.5, 0.5));
        await Tick();
        Assert.False(lease.UserInput.IsCancellationRequested); // nothing to click on

        _bridge.Post(new BridgeCommand(BridgeCommandKind.Home)); // works without a window
        await Tick();
        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.Empty(_sink.Frames);

        await lease.DisposeAsync();
        await Tick();
        Assert.NotEmpty(_sink.Frames);
    }

    [Fact]
    public async Task ClicksHeldBeforeTheWindowWasLostAreDroppedLikeAnyPendingInput()
    {
        var lease = await AcquireAsync();
        Press(At(Portrait, 0.5, 0.5));
        Release(At(Portrait, 0.5, 0.5));
        await Tick();
        await lease.DisposeAsync();
        _video.Window = 0;

        await Tick();

        Assert.Empty(_sink.Frames);
        Assert.False(_arbiter.UserInputActive);
    }

    // ---- the token -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTokenFiresOnceAndItsCallbacksDoNotRunInsideTheNotifyingCall()
    {
        var lease = await AcquireAsync();
        var calls = 0;
        using var returned = new ManualResetEventSlim();
        var ranAfterReturn = false;
        using var registration = lease.UserInput.Register(() =>
        {
            // If this ran inside NotifyUserInput on the notifying thread, 'returned' could never be set meanwhile.
            ranAfterReturn = returned.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref calls);
        });

        var notifier = new Thread(() =>
        {
            _arbiter.NotifyUserInput();
            _arbiter.NotifyUserInput();
            returned.Set();
        });
        notifier.Start();
        notifier.Join();

        await Eventually(() => Volatile.Read(ref calls) >= 1, "the callback");
        await Task.Delay(20);
        Assert.True(ranAfterReturn);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(lease.UserInput.IsCancellationRequested);
    }

    // ---- the loop ends (Close) -------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheLoopStopsAPendingAcquisitionIsRefusedAsClosedAndLaterOnesToo()
    {
        var pending = _arbiter.TryAcquireAsync().AsTask();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await _bridge.RunAsync(cts.Token); // ends at once; its finally closes the arbiter

        Assert.Equal(ApiInputRefusal.Closed, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Refusal);
        Assert.Equal(ApiInputRefusal.Closed, (await _arbiter.TryAcquireAsync()).Refusal);
        Assert.False(_arbiter.HoldsInput);
    }

    [Fact]
    public async Task ClosingInterruptsAHeldLeaseAndItsDisposalIsHarmless()
    {
        var lease = await AcquireAsync();

        _arbiter.Close();
        _arbiter.Close(); // idempotent

        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.False(_arbiter.HoldsInput);
        Assert.Null(_arbiter.LastUserInput); // not the user's doing
        await lease.DisposeAsync();
        Assert.Equal(ApiInputRefusal.Closed, (await _arbiter.TryAcquireAsync()).Refusal);
    }

    [Fact]
    public async Task OnlyAClosedArbiterMarksTheLeaseAsEndedWithTheSession()
    {
        var lease = await AcquireAsync();
        Assert.False(lease.SessionEnded);

        Assert.False(lease.UserInput.IsCancellationRequested);
        _arbiter.Close();

        Assert.True(lease.SessionEnded); // set before the token fires: whoever sees the token cancelled can tell why
        Assert.True(lease.UserInput.IsCancellationRequested);
    }

    [Fact]
    public async Task ClosingLetsAKeyThatWaitsForTheLeaseThroughAsAnOrdinaryKey()
    {
        StartRelay();
        var lease = await AcquireAsync();
        _keys.Writer.TryWrite(Down(65, 0x04));
        _keys.Writer.TryWrite(Up(65));
        await Eventually(() => lease.UserInput.IsCancellationRequested, "the interruption");

        _arbiter.Close();

        await Eventually(() => _keySink.Reports.Count >= 2, "the keys");
        Assert.Equal([new KeyboardReport(0, [0x04]), KeyboardReport.Released], _keySink.Reports);
    }

    // ---- the probe and a waiting key -------------------------------------------------------------------------------

    [Fact]
    public async Task AKeyWaitingForTheLeaseIsDroppedWhenTheProbeTakesOverAndLeavesNothingHeld()
    {
        StartRelay();
        var lease = await AcquireAsync();
        _keys.Writer.TryWrite(Down(65, 0x04));
        await Eventually(() => lease.UserInput.IsCancellationRequested, "the interruption");
        var suspended = _bridge.SuspendAsync();
        await Tick();
        _keys.Writer.TryWrite(Up(65)); // its release must not turn into anything either

        await lease.DisposeAsync();
        await Tick();
        await suspended;
        await Eventually(() => !_arbiter.UserInputActive, "the waiting key to be dropped"); // before the probe is done
        _bridge.Resume();
        await Tick();

        // A later key proves the relay got through the dropped press and its release without sending anything.
        _keys.Writer.TryWrite(Down(66, 0x05));
        _keys.Writer.TryWrite(Up(66));
        await Eventually(() => _keySink.Reports.Count >= 2, "the later key");
        Assert.Equal([new KeyboardReport(0, [0x05]), KeyboardReport.Released], _keySink.Reports);
    }


    [Fact]
    public async Task DisposingTwiceIsHarmlessAndAnOldLeaseCannotReleaseANewOne()
    {
        var first = await AcquireAsync();
        await first.DisposeAsync();
        await first.DisposeAsync();
        await Tick();

        await using var second = await AcquireAsync();
        await first.DisposeAsync(); // stale

        Assert.True(_arbiter.HoldsInput);
        Assert.Equal(ApiInputRefusal.Busy, (await _arbiter.TryAcquireAsync()).Refusal);
    }

    [Fact]
    public async Task TheInputCanBeAcquiredAgainAfterTheLeaseWasGivenBack()
    {
        var first = await AcquireAsync();
        await first.DisposeAsync();
        await Tick();

        await using var second = await AcquireAsync();

        Assert.False(second.UserInput.IsCancellationRequested);
    }

    [Fact]
    public async Task AcquiringRightAfterAReleaseDoesNotLoseWhatTheUserDidMeanwhile()
    {
        var p = At(Portrait, 0.5, 0.5);
        var lease = await AcquireAsync();
        Press(p);
        Release(p);
        await Tick();
        await lease.DisposeAsync();

        // The bridge has not ticked since: the user's held click must not be skipped by the next lease.
        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());
        await Tick();
        Assert.Equal(TapFrames, _sink.Frames.Select(Kind));
    }

    // ---- LastUserInput and UserInputActive --------------------------------------------------------------------------

    [Fact]
    public async Task LastUserInputIsStampedByTheUsersClicksWheelShortcutsAndCommandsInNormalMode()
    {
        _desktop.CountsPresses = true;
        Assert.Null(_arbiter.LastUserInput);

        _desktop.CursorPosition = At(Portrait, 0.5, 0.5); // a cursor move is not input
        await Tick();
        Assert.Null(_arbiter.LastUserInput);

        await StampedBy(() => Press(At(Portrait, 0.5, 0.5)));
        await StampedBy(() => Release(At(Portrait, 0.5, 0.5)));
        await StampedBy(() => _mouse.Writer.TryWrite(MouseInputEvent.Wheel(At(Portrait, 0.5, 0.5), -120)));
        await _bridge.Engine.AbortAsync();
        await StampedBy(() => _desktop.Presses[ShortcutKey.Home] = 1);
        await StampedBy(() => _desktop.Presses[ShortcutKey.Rotate] = 1);
        await StampedBy(() => _bridge.Post(new BridgeCommand(BridgeCommandKind.ToggleRotation)));
    }

    private async Task StampedBy(Action input)
    {
        _time.Advance(TimeSpan.FromSeconds(10));
        var before = _time.GetUtcNow();
        input();
        await Tick();
        Assert.True(_arbiter.LastUserInput >= before, "the input was not stamped");
        Assert.True(_arbiter.LastUserInput <= _time.GetUtcNow());
    }

    [Fact]
    public async Task APressThatIsNotATouchIsNotStamped()
    {
        Press(new PointD(305, 450));
        _mouse.Writer.TryWrite(MouseInputEvent.Press(MouseButton.Left, At(Portrait, 0.5, 0.5), alt: true));
        await Tick();

        Assert.Null(_arbiter.LastUserInput);
    }

    [Fact]
    public async Task TheInterruptionIsStampedWhenTheUserActsDuringApiMode()
    {
        var lease = await AcquireAsync();
        _time.Advance(TimeSpan.FromSeconds(3));
        var at = _time.GetUtcNow();

        Press(At(Portrait, 0.5, 0.5));
        await Tick();

        Assert.Equal(at, _arbiter.LastUserInput); // the clock stood still: stamped at the interruption, not at the replay
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task UserInputIsActiveWhileTheButtonIsHeldAndNotAfter()
    {
        var p = At(Portrait, 0.5, 0.5);
        Assert.False(_arbiter.UserInputActive);

        Press(p);
        await Tick();
        Assert.True(_arbiter.UserInputActive);

        Release(p);
        await Tick();
        Assert.False(_arbiter.UserInputActive);
    }

    // ---- the probe (Suspended) -------------------------------------------------------------------------------------

    [Fact]
    public async Task AProbeSuspensionInterruptsTheLeaseAndCompletesOnlyAfterItIsGivenBack()
    {
        var lease = await AcquireAsync();

        var suspended = _bridge.SuspendAsync();
        await Tick();

        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.False(suspended.IsCompleted);
        Assert.False(_bridge.Status.Suspended);
        Assert.Null(_arbiter.LastUserInput); // not the user's doing

        await lease.DisposeAsync();
        await Tick();

        await suspended.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_bridge.Status.Suspended);
    }

    [Fact]
    public async Task AcquisitionIsRefusedWhileTheBridgeIsSuspendedOrAboutToBe()
    {
        var suspended = _bridge.SuspendAsync();
        Assert.Equal(ApiInputRefusal.Suspended, (await _arbiter.TryAcquireAsync()).Refusal); // asked, not yet ticked

        await Tick();
        await suspended;
        Assert.Equal(ApiInputRefusal.Suspended, (await _arbiter.TryAcquireAsync()).Refusal);

        _bridge.Resume();
        await Tick();
        await using var lease = await AcquireAsync();
    }

    [Fact]
    public async Task ASuspensionRequestedWhileTheLeaseIsBeingTakenRefusesIt()
    {
        var pending = _arbiter.TryAcquireAsync().AsTask();

        _ = _bridge.SuspendAsync();
        await Tick();

        Assert.Equal(ApiInputRefusal.Suspended, (await pending).Refusal);
        Assert.False(_arbiter.HoldsInput);
    }

    [Fact]
    public async Task WhatWasHeldBackIsDroppedWhenTheProbeTakesOver()
    {
        var lease = await AcquireAsync();
        Press(At(Portrait, 0.5, 0.5));
        Release(At(Portrait, 0.5, 0.5));
        var suspended = _bridge.SuspendAsync();
        await Tick();
        await lease.DisposeAsync();
        await Tick();
        await suspended;

        _bridge.Resume();
        await Tick();

        Assert.Empty(_sink.Frames);
    }

    // ---- the arbiter's key side ------------------------------------------------------------------------------------

    [Fact]
    public async Task AModifierOrAStrayReleaseWouldNotReachTheDeviceAndDoesNotInterrupt()
    {
        await using var lease = await AcquireAsync();

        var admission = await _arbiter.BeginKeyEventAsync(false, CancellationToken.None);

        Assert.Equal(ApiInputArbiter.KeyAdmission.StateOnly, admission);
        Assert.False(lease.UserInput.IsCancellationRequested);
        Assert.Null(_arbiter.LastUserInput);
    }

    [Fact]
    public async Task AWaitingKeyEventIsCancelledWithTheRelayAndLeavesNothingPending()
    {
        await using var lease = await AcquireAsync();
        using var cts = new CancellationTokenSource();

        var waiting = _arbiter.BeginKeyEventAsync(true, cts.Token).AsTask();
        Assert.True(lease.UserInput.IsCancellationRequested);
        Assert.True(_arbiter.UserInputActive);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(_arbiter.UserInputActive);
    }

    // ---- the keyboard relay in API mode ----------------------------------------------------------------------------

    [Fact]
    public async Task KeysInApiModeInterruptAndAreSentInOrderAfterTheLeaseIsDisposed()
    {
        StartRelay();
        var lease = await AcquireAsync();

        _keys.Writer.TryWrite(Down(65, 0x04));
        _keys.Writer.TryWrite(Up(65));
        _keys.Writer.TryWrite(Down(66, 0x05));
        await Eventually(() => lease.UserInput.IsCancellationRequested, "the interruption");

        Assert.Empty(_keySink.Reports);
        Assert.True(_arbiter.UserInputActive);
        Assert.NotNull(_arbiter.LastUserInput);

        await lease.DisposeAsync();
        await Eventually(() => _keySink.Reports.Count >= 3, "the held keys");

        Assert.Equal([new KeyboardReport(0, [0x04]), KeyboardReport.Released, new KeyboardReport(0, [0x05])], _keySink.Reports);
        Assert.True(_arbiter.UserInputActive); // the second key is down
    }

    [Fact]
    public async Task AModifierPressedBeforeTheInterruptingKeyIsKept()
    {
        StartRelay();
        var lease = await AcquireAsync();

        _keys.Writer.TryWrite(new KeyboardInputEvent(17, true, new HidKey(0, HidModifiers.LeftGui), KeyRole.Modifier));
        _keys.Writer.TryWrite(Down(67, 0x06));
        await Eventually(() => lease.UserInput.IsCancellationRequested, "the interruption");
        await lease.DisposeAsync();
        await Eventually(() => _keySink.Reports.Count >= 1, "the key");

        Assert.Equal(new KeyboardReport(HidModifiers.LeftGui, [0x06]), _keySink.Reports[0]);
    }

    [Fact]
    public async Task KeysAreSentAsBeforeWithoutALease()
    {
        StartRelay();

        _keys.Writer.TryWrite(Down(65, 0x04));
        _keys.Writer.TryWrite(Up(65));
        await Eventually(() => _keySink.Reports.Count >= 2, "the reports");

        Assert.Equal([new KeyboardReport(0, [0x04]), KeyboardReport.Released], _keySink.Reports);
        Assert.NotNull(_arbiter.LastUserInput);
        await Eventually(() => !_arbiter.UserInputActive, "the keys to be released");
    }

    [Fact]
    public async Task AKeyWaitingForTheLeaseBlocksANewLeaseUntilItHasBeenSent()
    {
        StartRelay();
        var lease = await AcquireAsync();
        _keys.Writer.TryWrite(Down(65, 0x04));
        await Eventually(() => lease.UserInput.IsCancellationRequested, "the interruption");

        await lease.DisposeAsync();

        // Whether the relay woke yet or not, the held key keeps the next lease away.
        Assert.Equal(ApiInputRefusal.UserActive, await RefusedAsync());
        await Eventually(() => _keySink.Reports.Count >= 1, "the held key");
    }

    [Fact]
    public async Task StoppingTheRelayWhileAKeyWaitsDoesNotHang()
    {
        StartRelay();
        var lease = await AcquireAsync();
        _keys.Writer.TryWrite(Down(65, 0x04));
        await Eventually(() => lease.UserInput.IsCancellationRequested, "the interruption");

        await _stop.CancelAsync();
        await _relay.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_arbiter.UserInputActive);
        await lease.DisposeAsync();
    }

    private sealed class RecordingKeys : IKeyboardSink
    {
        private readonly List<KeyboardReport> _reports = [];

        public bool HasKeyboard => true;

        public List<KeyboardReport> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            lock (_reports)
            {
                _reports.Add(new KeyboardReport(modifiers, keys.ToArray()));
            }

            return ValueTask.CompletedTask;
        }
    }
}
