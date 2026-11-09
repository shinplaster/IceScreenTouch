using System.Diagnostics;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using IceScreenTouch.Video;

namespace IceScreenTouch.Windows.Tests;

public class ClickSwallowerTests
{
    private const nint Mirror = 0x100;
    private const nint Other = 0x200;
    private const long Now = 1000;

    private static readonly RectD Picture = new(300, 150, 400, 600);
    private static readonly SwallowZone Zone = new(Mirror, Picture, Now);

    private nint _under = Mirror;
    private int _lookups;

    private ClickSwallower Create() => new((_, _) =>
    {
        _lookups++;
        return _under;
    });

    private static MouseInputEvent Down(double x, double y, MouseButton b = MouseButton.Left) => MouseInputEvent.Press(b, new PointD(x, y));

    private static MouseInputEvent Up(double x, double y, MouseButton b = MouseButton.Left) => MouseInputEvent.Release(b, new PointD(x, y));

    [Fact]
    public void APressOverThePictureOfTheMirrorWindowIsSwallowed()
    {
        var swallower = Create();

        Assert.True(swallower.ShouldSwallow(Down(400, 300), Zone, Now));
        Assert.True(swallower.ShouldSwallow(Down(400, 300, MouseButton.Right), Zone, Now));
    }

    [Fact]
    public void APressOutsideThePictureIsPassedOnWithoutLookingAtWindows()
    {
        var swallower = Create();

        Assert.False(swallower.ShouldSwallow(Down(100, 300), Zone, Now)); // the title bar, or the desktop
        Assert.False(swallower.ShouldSwallow(Down(700, 300), Zone, Now)); // the right edge is outside
        Assert.Equal(0, _lookups);
    }

    [Fact]
    public void APressWhereAnotherWindowIsOnTopIsPassedOn()
    {
        _under = Other;

        Assert.False(Create().ShouldSwallow(Down(400, 300), Zone, Now));

        _under = 0;
        Assert.False(Create().ShouldSwallow(Down(400, 300), Zone, Now));
    }

    [Fact]
    public void WithoutAZoneNothingIsSwallowed()
    {
        // The quirk is off, the bridge is suspended or there is no picture: the publisher gives no zone.
        var swallower = Create();

        Assert.False(swallower.ShouldSwallow(Down(400, 300), null, Now));
        Assert.False(swallower.ShouldSwallow(Up(400, 300), null, Now));
        Assert.Equal(0, _lookups);
    }

    [Fact]
    public void TheReleaseOfASwallowedPressIsSwallowedWhereverTheCursorIs()
    {
        var swallower = Create();

        Assert.True(swallower.ShouldSwallow(Down(400, 300), Zone, Now));
        _under = Other;
        Assert.True(swallower.ShouldSwallow(Up(2000, 40), null, Now)); // dragged out of the picture, zone gone meanwhile
        Assert.False(swallower.ShouldSwallow(Up(2000, 40), null, Now)); // only once
    }

    [Fact]
    public void TheReleaseOfAPressThatWentOnGoesOn()
    {
        var swallower = Create();

        Assert.False(swallower.ShouldSwallow(Down(100, 300), Zone, Now));
        Assert.False(swallower.ShouldSwallow(Up(400, 300), Zone, Now)); // released over the picture: the press was not swallowed
    }

    [Fact]
    public void TheButtonsAreMatchedOneByOne()
    {
        var swallower = Create();

        Assert.True(swallower.ShouldSwallow(Down(400, 300), Zone, Now));
        Assert.False(swallower.ShouldSwallow(Up(400, 300, MouseButton.Right), Zone, Now)); // no right press was swallowed
        Assert.True(swallower.ShouldSwallow(Up(400, 300), Zone, Now));
    }

    [Fact]
    public void AnAltPressAndItsReleaseGoToTheReceiver()
    {
        var swallower = Create();

        Assert.False(swallower.ShouldSwallow(MouseInputEvent.Press(MouseButton.Left, new PointD(400, 300), alt: true), Zone, Now));
        Assert.False(swallower.ShouldSwallow(Up(400, 300), Zone, Now)); // the release comes without Alt, and goes on with its press
        Assert.False(swallower.ShouldSwallow(MouseInputEvent.Press(MouseButton.Right, new PointD(400, 300), alt: true), Zone, Now));
        Assert.Equal(0, _lookups);

        // Without Alt the same press is swallowed again.
        Assert.True(swallower.ShouldSwallow(Down(400, 300), Zone, Now));
    }

    [Fact]
    public void AnAltPressAfterASwallowedOneForgetsIt()
    {
        var swallower = Create();

        Assert.True(swallower.ShouldSwallow(Down(400, 300), Zone, Now));
        Assert.False(swallower.ShouldSwallow(MouseInputEvent.Press(MouseButton.Left, new PointD(400, 300), alt: true), Zone, Now)); // the release was lost
        Assert.False(swallower.ShouldSwallow(Up(400, 300), Zone, Now));
    }

    [Fact]
    public void AReleaseNeverSwallowsWithoutItsPressEvenOverThePicture()
    {
        Assert.False(Create().ShouldSwallow(Up(400, 300), Zone, Now));
    }

    [Fact]
    public void ANewPressForgetsAMissedRelease()
    {
        var swallower = Create();

        Assert.True(swallower.ShouldSwallow(Down(400, 300), Zone, Now));
        _under = Other;
        Assert.False(swallower.ShouldSwallow(Down(400, 300), Zone, Now)); // the release was lost; this press goes on
        Assert.False(swallower.ShouldSwallow(Up(400, 300), Zone, Now)); // so its release does too
    }

    [Fact]
    public void TheWheelIsNeverSwallowed()
    {
        Assert.False(Create().ShouldSwallow(MouseInputEvent.Wheel(new PointD(400, 300), 120), Zone, Now));
        Assert.False(Create().ShouldSwallow(MouseInputEvent.Wheel(new PointD(400, 300), 120, horizontal: true), Zone, Now));
    }
}

public class SwallowZonePublisherTests
{
    private static readonly RectD Picture = new(300, 150, 400, 600);
    private static readonly long Second = Stopwatch.Frequency;

    private readonly List<SwallowZone?> _published = [];
    private long _now = 5 * Stopwatch.Frequency;
    private int _rootLookups;

    private SwallowZonePublisher Create() => new(
        _published.Add,
        w =>
        {
            _rootLookups++;
            return w + 1; // a root that differs from the window, as a UWP frame does
        },
        () => _now);

    private static BridgeStatus Status(nint window = 0x10, RectD? picture = null, bool suspended = false) =>
        BridgeStatus.Initial with { Window = window, Picture = picture ?? Picture, Suspended = suspended };

    [Fact]
    public void ThePictureAndTheTopLevelWindowBecomeTheZoneStampedWithTheClock()
    {
        Create().Publish(Status());

        Assert.Equal([new SwallowZone(0x11, Picture, _now)], _published);
    }

    [Fact]
    public void AnUnchangedStatusIsNotLookedUpAgainAndOnlyRepublishedAsAHeartbeat()
    {
        var publisher = Create();

        publisher.Publish(Status());
        for (var i = 0; i < 5; i++)
        {
            _now += Second / 1000 * 4; // a bridge tick
            publisher.Publish(Status());
        }

        Assert.Single(_published);
        Assert.Equal(1, _rootLookups);

        _now += Second / 2; // past the heartbeat: the zone must not grow old while the bridge runs
        publisher.Publish(Status());

        Assert.Equal(2, _published.Count);
        Assert.Equal(_now, _published[1]!.Stamp);
        Assert.Equal(1, _rootLookups);
        Assert.True(_published[1]!.IsFresh(_now + (Second / 2)));
    }

    [Fact]
    public void ANullZoneIsNotRepeated()
    {
        var publisher = Create();

        publisher.Publish(BridgeStatus.Initial);
        _now += Second;
        publisher.Publish(BridgeStatus.Initial);

        Assert.Equal([null], _published);
    }

    [Fact]
    public void AMovedPictureIsPublishedAnew()
    {
        var publisher = Create();

        publisher.Publish(Status());
        publisher.Publish(Status(picture: Picture with { Left = 310 }));

        Assert.Equal(new SwallowZone(0x11, Picture with { Left = 310 }, _now), _published[^1]);
        Assert.Equal(2, _published.Count);
    }

    [Fact]
    public void ASuspendedBridgeSwallowsNothing()
    {
        var publisher = Create();

        publisher.Publish(Status());
        publisher.Publish(Status(suspended: true));
        publisher.Publish(Status());

        Assert.Equal([new SwallowZone(0x11, Picture, _now), null, new SwallowZone(0x11, Picture, _now)], _published);
    }

    [Fact]
    public void NoWindowOrNoPictureSwallowsNothing()
    {
        var publisher = Create();

        publisher.Publish(BridgeStatus.Initial);
        publisher.Publish(Status(picture: RectD.Empty));
        publisher.Publish(Status() with { Picture = null });

        Assert.All(_published, z => Assert.Null(z));
        Assert.Equal(3, _published.Count);
    }

    [Fact]
    public void AWindowWithoutATopLevelWindowSwallowsNothing()
    {
        var publisher = new SwallowZonePublisher(_published.Add, _ => 0, () => _now);

        publisher.Publish(Status());

        Assert.Equal([null], _published);
    }

    [Fact]
    public void StopTakesTheZoneAwayAndLaterStatusesChangeNothing()
    {
        var publisher = Create();
        publisher.Publish(Status());

        publisher.Stop();
        _now += Second;
        publisher.Publish(Status());
        publisher.Publish(Status(picture: Picture with { Left = 1 }));

        Assert.Equal([new SwallowZone(0x11, Picture, 5 * Second), null], _published);
    }
}

public class SwallowZoneAgeTests
{
    private static readonly long Second = Stopwatch.Frequency;
    private static readonly RectD Picture = new(300, 150, 400, 600);

    [Fact]
    public void AZoneIsFreshForAboutASecondAndAHalf()
    {
        var zone = new SwallowZone(0x100, Picture, 10 * Second);

        Assert.True(zone.IsFresh(10 * Second));
        Assert.True(zone.IsFresh(11 * Second));
        Assert.False(zone.IsFresh((12 * Second) + 1));
    }

    [Fact]
    public void AStaleZoneSwallowsNothingAndItsReleaseGoesOn()
    {
        var swallower = new ClickSwallower((_, _) => 0x100);
        var zone = new SwallowZone(0x100, Picture, 10 * Second);

        Assert.True(swallower.ShouldSwallow(MouseInputEvent.Press(MouseButton.Left, new PointD(400, 300)), zone, 11 * Second));
        Assert.True(swallower.ShouldSwallow(MouseInputEvent.Release(MouseButton.Left, new PointD(400, 300)), zone, 11 * Second));
        Assert.False(swallower.ShouldSwallow(MouseInputEvent.Press(MouseButton.Left, new PointD(400, 300)), zone, 13 * Second));
        Assert.False(swallower.ShouldSwallow(MouseInputEvent.Release(MouseButton.Left, new PointD(400, 300)), zone, 13 * Second));
    }

    [Fact]
    public void ASwallowedPressStillGetsItsReleaseSwallowedAfterTheZoneWentStale()
    {
        var swallower = new ClickSwallower((_, _) => 0x100);
        var zone = new SwallowZone(0x100, Picture, 10 * Second);

        Assert.True(swallower.ShouldSwallow(MouseInputEvent.Press(MouseButton.Left, new PointD(400, 300)), zone, 11 * Second));
        Assert.True(swallower.ShouldSwallow(MouseInputEvent.Release(MouseButton.Left, new PointD(400, 300)), zone, 20 * Second));
    }
}

public class MouseHookProcessTests
{
    private static readonly RectD Picture = new(300, 150, 400, 600);
    private static readonly SwallowZone Zone = new(0x100, Picture, 1000);

    private readonly List<MouseInputEvent> _reported = [];
    private readonly List<nint> _swallowed = [];
    private nint _under = 0x100;
    private bool _altDown;
    private bool _inputThrows;

    private MouseHook Create(bool withZone = true)
    {
        var hook = new MouseHook(
            e =>
            {
                _reported.Add(e);
                if (_inputThrows)
                {
                    throw new InvalidOperationException("the channel is gone");
                }
            },
            _swallowed.Add,
            new ClickSwallower((_, _) => _under),
            vk => vk == NativeMethods.VkMenu && _altDown);
        if (withZone)
        {
            hook.SwallowZone = Zone;
        }

        return hook;
    }

    private static NativeMethods.MsLlHookStruct At(int x, int y) => new() { Pt = new NativeMethods.Point { X = x, Y = y } };

    [Fact]
    public void ASwallowedPressIsReportedAndTellsTheZonesWindow()
    {
        using var hook = Create();

        Assert.True(hook.Process(NativeMethods.WmLButtonDown, At(400, 300), 1000));

        Assert.Equal([MouseInputEvent.Press(MouseButton.Left, new PointD(400, 300))], _reported);
        Assert.Equal([(nint)0x100], _swallowed);
    }

    [Fact]
    public void OnlyTheMatchingReleaseIsSwallowedAndItDoesNotTellAnyone()
    {
        using var hook = Create();

        Assert.False(hook.Process(NativeMethods.WmRButtonUp, At(400, 300), 1000)); // no press before it
        Assert.True(hook.Process(NativeMethods.WmLButtonDown, At(400, 300), 1000));
        Assert.False(hook.Process(NativeMethods.WmRButtonUp, At(400, 300), 1000)); // the other button
        Assert.True(hook.Process(NativeMethods.WmLButtonUp, At(900, 900), 1000));
        Assert.False(hook.Process(NativeMethods.WmLButtonUp, At(900, 900), 1000)); // only once

        Assert.Equal(4, _reported.Count(e => e.Kind == MouseInputKind.Release));
        Assert.Single(_swallowed);
    }

    [Fact]
    public void WithoutAZoneEverythingIsPassedOnButStillReported()
    {
        using var hook = Create(withZone: false);

        Assert.False(hook.Process(NativeMethods.WmLButtonDown, At(400, 300), 1000));
        Assert.False(hook.Process(NativeMethods.WmLButtonUp, At(400, 300), 1000));

        Assert.Equal(2, _reported.Count);
        Assert.Empty(_swallowed);
    }

    [Fact]
    public void AnInputCallbackThatThrowsChangesNeitherTheDecisionNorTheNotice()
    {
        _inputThrows = true;
        using var hook = Create();

        Assert.True(hook.Process(NativeMethods.WmLButtonDown, At(400, 300), 1000));
        Assert.True(hook.Process(NativeMethods.WmLButtonUp, At(400, 300), 1000));
        Assert.False(hook.Process(NativeMethods.WmLButtonDown, At(10, 10), 1000));

        Assert.Equal([(nint)0x100], _swallowed);
    }

    [Fact]
    public void ACallbackForASwallowedPressThatThrowsStillSwallowsIt()
    {
        using var hook = new MouseHook(_ => { }, _ => throw new InvalidOperationException("boom"), new ClickSwallower((_, _) => 0x100))
        {
            SwallowZone = Zone,
        };

        Assert.True(hook.Process(NativeMethods.WmLButtonDown, At(400, 300), 1000));
    }

    [Fact]
    public void AltHeldAtThePressPassesItAndItsReleaseOn()
    {
        using var hook = Create();
        _altDown = true;

        Assert.False(hook.Process(NativeMethods.WmLButtonDown, At(400, 300), 1000));
        _altDown = false; // let go of Alt before the button
        Assert.False(hook.Process(NativeMethods.WmLButtonUp, At(400, 300), 1000));

        Assert.True(_reported[0].Alt);
        Assert.Empty(_swallowed);
    }

    [Fact]
    public void DisposingTakesTheZoneAway()
    {
        var hook = Create();

        hook.Dispose();

        Assert.Null(hook.SwallowZone);
    }

    [Fact]
    public void AStaleZoneIsIgnored()
    {
        using var hook = Create();

        Assert.False(hook.Process(NativeMethods.WmLButtonDown, At(400, 300), 1000 + (10 * Stopwatch.Frequency)));
    }

    [Fact]
    public void TranslateReadsAltAtTheButtonPressesWithTheInjectedKeyState()
    {
        Assert.True(MouseHook.Translate(NativeMethods.WmLButtonDown, At(1, 2), vk => vk == NativeMethods.VkMenu).Alt);
        Assert.True(MouseHook.Translate(NativeMethods.WmRButtonDown, At(1, 2), vk => vk == NativeMethods.VkMenu).Alt);
        Assert.False(MouseHook.Translate(NativeMethods.WmLButtonDown, At(1, 2), _ => false).Alt);
        Assert.False(MouseHook.Translate(NativeMethods.WmLButtonDown, At(1, 2), vk => vk == NativeMethods.VkControl).Alt);
        Assert.True(MouseHook.Translate(NativeMethods.WmLButtonDown, At(1, 2), vk => vk == NativeMethods.VkControl).Control);
        Assert.False(MouseHook.Translate(NativeMethods.WmLButtonUp, At(1, 2), _ => true).Alt); // read at the press only
    }
}

public class ForegroundActivatorTests
{
    private const nint Mirror = 0x100;
    private const nint Other = 0x200;

    private readonly List<string> _log = [];
    private readonly List<string> _calls = [];
    private nint _foreground = Other;
    private bool _directWorks;
    private bool _attachedWorks;
    private bool _attachSucceeds = true;
    private uint _foregroundThread = 77;
    private readonly List<Action> _queued = [];
    private long _clock = Stopwatch.Frequency;

    private ForegroundActivator Create() => new(
        new ForegroundActivator.ForegroundOperations(
            () => _foreground,
            w =>
            {
                _calls.Add("set");
                if (_directWorks || (_attachedWorks && _calls.Contains("attach"))) { _foreground = w; }
                return true;
            },
            _ =>
            {
                _calls.Add("top");
                return true;
            },
            () => _foregroundThread,
            () => 5,
            (_, _, attach) =>
            {
                _calls.Add(attach ? "attach" : "detach");
                return _attachSucceeds;
            }),
        _log.Add,
        _queued.Add,
        () => _clock);

    [Fact]
    public void ARequestWhileOneIsBeingServedIsDropped()
    {
        _directWorks = true;
        var activator = Create();

        activator.RequestAsync(Mirror);
        activator.RequestAsync(Mirror);
        _clock += Stopwatch.Frequency; // a second later it is still being served
        activator.RequestAsync(Mirror);

        Assert.Single(_queued);

        _queued[0](); // served
        _foreground = Other;
        activator.RequestAsync(Mirror);

        Assert.Equal(2, _queued.Count);
    }

    [Fact]
    public void ARequestThatTakesMoreThanAFewSecondsIsSupersededByTheNextOne()
    {
        _directWorks = true;
        var activator = Create();

        activator.RequestAsync(Mirror); // hangs: never run
        _clock += 4 * Stopwatch.Frequency;
        activator.RequestAsync(Mirror);

        Assert.Equal(2, _queued.Count);

        // The hung one finishing late does not free the place of the newer one.
        _queued[0]();
        activator.RequestAsync(Mirror);
        Assert.Equal(2, _queued.Count);

        _queued[1]();
        activator.RequestAsync(Mirror);
        Assert.Equal(3, _queued.Count);
    }

    [Fact]
    public void AWindowInFrontIsLeftAlone()
    {
        _foreground = Mirror;

        Assert.Equal(ActivationMethod.AlreadyForeground, Create().Activate(Mirror));

        Assert.Empty(_calls);
        Assert.Empty(_log);
    }

    [Fact]
    public void SetForegroundWindowIsTriedFirstAndNothingIsAttachedWhenItWorks()
    {
        _directWorks = true;

        Assert.Equal(ActivationMethod.SetForegroundWindow, Create().Activate(Mirror));

        Assert.Equal(["set"], _calls);
        Assert.Contains("SetForegroundWindow", Assert.Single(_log), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenWindowsRefusesTheInputIsAttachedForTheCallAndDetachedAgain()
    {
        _attachedWorks = true;

        Assert.Equal(ActivationMethod.AttachThreadInput, Create().Activate(Mirror));

        Assert.Equal(["set", "attach", "top", "set", "detach"], _calls);
        Assert.Contains("AttachThreadInput", Assert.Single(_log), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenBothFailThatIsLoggedAndTheInputIsDetached()
    {
        Assert.Equal(ActivationMethod.Failed, Create().Activate(Mirror));

        Assert.Equal(["set", "attach", "top", "set", "detach"], _calls);
        var line = Assert.Single(_log);
        Assert.Contains("could not", line, StringComparison.Ordinal);
        Assert.Contains("Alt + click", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAttachThatIsRefusedOrNoForegroundThreadIsAFailureWithoutDetaching()
    {
        _attachSucceeds = false;
        Assert.Equal(ActivationMethod.Failed, Create().Activate(Mirror));
        Assert.Equal(["set", "attach"], _calls);

        _calls.Clear();
        _attachSucceeds = true;
        _foregroundThread = 0;
        Assert.Equal(ActivationMethod.Failed, Create().Activate(Mirror));
        Assert.Equal(["set"], _calls);
    }

    [Fact]
    public void EachOutcomeIsLoggedOnlyOncePerSession()
    {
        _attachedWorks = true;
        var activator = Create();

        _ = activator.Activate(Mirror);
        _foreground = Other;
        _calls.Clear();
        _ = activator.Activate(Mirror);

        Assert.Single(_log);
    }
}

public class HostedWindowsTests
{
    private static HostedWindows.Info Core(nint window, int pid, int width = 1200, int height = 900, bool shown = true) => new(window, pid, shown, width, height);

    [Fact]
    public void TheBiggestShownWindowOfTheProcessIsChosen()
    {
        Assert.Equal(0x30, HostedWindows.Choose([Core(0x10, 5, 400, 300), Core(0x30, 5, 1200, 900), Core(0x20, 5, 800, 600)], 5));
    }

    [Fact]
    public void WindowsOfOtherProcessesHiddenOrEmptyOnesAreNotChosen()
    {
        Assert.Equal(0x10, HostedWindows.Choose(
            [Core(0x40, 9), Core(0x20, 5, shown: false), Core(0x30, 5, 0, 900), Core(0x35, 5, 1200, 0), Core(0x10, 5, 100, 100)],
            5));
        Assert.Equal(0, HostedWindows.Choose([Core(0x40, 9), Core(0x20, 5, shown: false)], 5));
        Assert.Equal(0, HostedWindows.Choose([], 5));
    }

    [Fact]
    public void AnEarlierWindowWinsATie()
    {
        Assert.Equal(0x10, HostedWindows.Choose([Core(0x10, 5), Core(0x20, 5)], 5));
    }

    [Fact]
    public void ANoiseProcessHasNoHostedWindow()
    {
        Assert.Equal(0, Win32Windows.LargestVisibleWindowOf(int.MaxValue - 2));
    }
}

public class ReceiverCatalogTests
{
    [Theory]
    [InlineData("Reflector4", true)]
    [InlineData("reflector4.exe", true)]
    [InlineData("Airplay", true)]
    [InlineData("AirServer", false)]
    [InlineData("LonelyScreen", false)]
    [InlineData("uxplay", false)]
    public void OnlyReceiversThatMoveTheirWindowOnADragAreKeptFromIt(string process, bool expected)
    {
        Assert.Equal(expected, ReceiverQuirks.MovesWindowOnDrag(process));
    }

    [Fact]
    public void TheNewReceiversAreKnownByTheProcessThatShowsThePicture()
    {
        var reflector = Assert.Single(KnownReceivers.All, k => k.Name == "Reflector 4");
        Assert.Equal("Reflector4", reflector.ProcessName);
        Assert.Equal([@"Reflector 4\Reflector4.exe"], reflector.InstallPaths);

        // 5KPlayer.exe is started; Airplay.exe, which it starts, owns the mirror window.
        var fiveK = Assert.Single(KnownReceivers.All, k => k.Name == "5KPlayer");
        Assert.Equal("Airplay", fiveK.ProcessName);
        Assert.Equal([@"DearMob\5KPlayer\5KPlayer.exe"], fiveK.InstallPaths);
        Assert.Null(fiveK.StoreLaunchAddress);
    }

    [Fact]
    public void TheTwoAirServersAreOneRowThatKnowsBothWaysToStartIt()
    {
        var airServer = Assert.Single(KnownReceivers.All, k => k.ProcessName == "AirServer");
        Assert.Equal([@"App Dynamic\AirServer\AirServer.exe"], airServer.InstallPaths);
        Assert.Equal(@"shell:AppsFolder\F3F176BD.AirServer_p8qzvses5c8me!AirServer.Desktop", airServer.StoreLaunchAddress);
    }

    [Theory]
    [InlineData("Airplay", "5KPlayer")]
    [InlineData("airplay.exe", "5KPlayer")]
    [InlineData("Reflector4", "Reflector 4")]
    [InlineData("AirServer", "AirServer")]
    [InlineData("SomeReceiver.exe", "SomeReceiver")]
    [InlineData("uxplay", "uxplay")]
    public void TheStatusShowsTheSameNameAsTheReceiverList(string process, string expected)
    {
        Assert.Equal(expected, KnownReceivers.DisplayName(process));
    }

    [Fact]
    public void EveryKnownReceiverIsListedOnceByNameAndByProcess()
    {
        Assert.Equal(KnownReceivers.All.Count, KnownReceivers.All.Select(k => k.Name).Distinct().Count());
        Assert.Equal(KnownReceivers.All.Count, KnownReceivers.All.Select(k => k.ProcessName).Distinct().Count());
        Assert.Contains(KnownReceivers.All, k => k.Name == "LonelyScreen");
    }
}
