// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using Microsoft.Extensions.Time.Testing;

#pragma warning disable CA2201 // the tests throw what an HRESULT of Windows becomes (OutOfMemoryException, COMException), and a plain Exception from a session

namespace IceScreenTouch.Windows.Tests;

// The decisions of the screenshot capture without Windows: the way that is taken (Windows.Graphics.Capture, else PrintWindow, else
// nothing), the mapping of the client area into a frame, and the session cache. Windows, Direct3D and the capture session are fakes.

/// <summary>A capture session that hands out frames made by the test.</summary>
internal sealed class FakeSession : IWgcSession
{
    private int _index = -1;

    /// <summary>The frames in the order they arrive: the read uses the first one, and one that must be newer moves on to the next.</summary>
    public List<FakeFrame> Frames { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Thrown by every read (a lost Direct3D device).</summary>
    public Exception? ReadFailure { get; init; }

    public List<(TimeSpan Timeout, bool RequireNew)> Reads { get; } = [];

    public WgcReadStatus Read<T>(WgcFrameReader<T> read, TimeSpan timeout, bool requireNewFrame, out T? result)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Reads.Add((timeout, requireNewFrame));
        result = default;
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        if (requireNewFrame ? _index + 1 >= Frames.Count : Frames.Count == 0)
        {
            return WgcReadStatus.NoFrame;
        }

        _index = requireNewFrame ? _index + 1 : Math.Max(_index, 0);
        var frame = Frames[_index];
        result = read(frame.Bgra, frame.Width, frame.Height, frame.Stride);
        return WgcReadStatus.Ok;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A frame whose pixel (x, y) is (x, y, 7, 255) in blue, green, red, alpha; a row has <paramref name="padding"/> bytes after its pixels.</summary>
internal sealed class FakeFrame
{
    public FakeFrame(int width, int height, int padding = 0)
    {
        (Width, Height, Stride) = (width, height, (width * 4) + padding);
        Bgra = new byte[(Stride * (height - 1)) + (width * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * Stride) + (x * 4);
                (Bgra[i], Bgra[i + 1], Bgra[i + 2], Bgra[i + 3]) = ((byte)x, (byte)y, 7, 255);
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Bgra { get; }
}

public sealed class WindowPictureCaptureTests
{
    private const nint Window = 0x1000;
    private const nint Root = 0x0F00;

    private sealed class FakeFacts : IWindowFacts
    {
        public WindowPresence Presence { get; set; } = WindowPresence.Shown;

        public WindowGeometry? Geometry { get; set; } = new WindowGeometry(Root, new RectI(100, 100, 10, 8), new RectI(102, 101, 6, 5));

        public WindowPresence PresenceOf(nint window) => Presence;

        public WindowGeometry? GeometryOf(nint window) => Geometry;
    }

    private sealed class FakePrint : IPrintCapture
    {
        public int Calls { get; private set; }

        public T? Print<T>(nint window, CapturedPixelsReader<T> read)
        {
            Calls++;
            var frame = new FakeFrame(6, 5);
            return read(frame.Bgra, frame.Width, frame.Height, frame.Stride);
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(bool supported = true, Func<nint, IWgcSession>? start = null)
        {
            Sessions = [];
            Start = start ?? (root =>
            {
                StartedFor.Add(root);
                var session = new FakeSession();
                session.Frames.Add(new FakeFrame(10, 8));
                Sessions.Add(session);
                return session;
            });
            Capture = new WindowPictureCapture(Time, Facts, Print, () => supported, root => Start(root), (kind, message) => Reports.Add((kind, message)));
        }

        public FakeTimeProvider Time { get; } = new();

        public FakeFacts Facts { get; } = new();

        public FakePrint Print { get; } = new();

        public List<FakeSession> Sessions { get; }

        public List<nint> StartedFor { get; } = [];

        public List<(string Kind, string Message)> Reports { get; } = [];

        public Func<nint, IWgcSession> Start { get; }

        public WindowPictureCapture Capture { get; }

        public PictureCaptureResult<(int W, int H, int Stride, byte First, byte Last)?> Take() =>
            Capture.Capture<(int, int, int, byte, byte)?>(Window, (bgra, w, h, stride) => (w, h, stride, bgra[0], bgra[((h - 1) * stride) + ((w - 1) * 4)]));

        public void Dispose() => Capture.Dispose();
    }

    [Fact]
    public void TheClientAreaIsCutOutOfTheFrameAtItsOffset()
    {
        // Frame 10 x 8 at (100, 100) with 2 bytes... of padding per row; the client area (102, 101) 6 x 5 is the frame's (2, 1) 6 x 5.
        using var h = new Harness(start: root =>
        {
            var s = new FakeSession();
            s.Frames.Add(new FakeFrame(10, 8, padding: 8));
            return s;
        });

        var shot = h.Take();

        Assert.Equal(PictureSource.GraphicsCapture, shot.Source);
        var seen = Assert.NotNull(shot.Value);
        Assert.Equal((6, 5, 48), (seen.W, seen.H, seen.Stride)); // the stride stays the frame's: 10 * 4 + 8
        Assert.Equal((byte)2, seen.First); // frame pixel (2, 1): blue is x
        Assert.Equal((byte)7, seen.Last); // frame pixel (7, 5): blue is x
        Assert.Equal(0, h.Print.Calls);
    }

    [Fact]
    public void EveryPixelOfTheClientAreaIsTheFramePixelAtTheOffset()
    {
        using var h = new Harness();
        var wrong = 0;

        var shot = h.Capture.Capture<int>(Window, (bgra, w, height, stride) =>
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * stride) + (x * 4);
                    if (bgra[i] != x + 2 || bgra[i + 1] != y + 1)
                    {
                        wrong++;
                    }
                }
            }

            return w * height;
        });

        Assert.Equal(30, shot.Value);
        Assert.Equal(0, wrong);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHiddenMinimizedOrCloakedWindowHasNoPictureAndIsNotPrintedEither(bool cloaked)
    {
        using var h = new Harness();
        h.Facts.Presence = cloaked ? WindowPresence.Cloaked : WindowPresence.Hidden;

        var shot = h.Take();

        Assert.Equal(PictureSource.None, shot.Source);
        Assert.Null(shot.Value);
        Assert.Equal(0, h.Print.Calls); // PrintWindow would make a window draw that is not drawn
        Assert.Empty(h.StartedFor);
        Assert.Equal(cloaked, h.Reports.Any(r => r.Kind == "cloaked"));
    }

    [Fact]
    public void WithoutWindowsGraphicsCaptureTheWindowIsPrinted()
    {
        using var h = new Harness(supported: false);

        var shot = h.Take();

        Assert.Equal(PictureSource.PrintWindow, shot.Source);
        Assert.NotNull(shot.Value);
        Assert.Equal(1, h.Print.Calls);
        Assert.Empty(h.StartedFor); // no session is tried
        Assert.Empty(h.Reports); // not being supported is not a failure to report on every shot
    }

    [Fact]
    public void ASessionThatCannotBeStartedFallsBackToPrintWindowAndIsLeftAloneFor30Seconds()
    {
        var starts = 0;
        using var h = new Harness(start: _ =>
        {
            starts++;
            throw new InvalidOperationException("no device");
        });

        var first = h.Take();
        var second = h.Take();

        Assert.Equal(PictureSource.PrintWindow, first.Source);
        Assert.Equal(PictureSource.PrintWindow, second.Source);
        Assert.Equal(1, starts); // the second shot did not try again
        Assert.Equal(2, h.Print.Calls);
        Assert.Single(h.Reports, r => r.Kind == "wgc-failed");

        h.Time.Advance(WindowPictureCapture.RetryAfterFailure - TimeSpan.FromMilliseconds(1));
        _ = h.Take();
        Assert.Equal(1, starts);

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        _ = h.Take();
        Assert.Equal(2, starts); // tried again
    }

    [Fact]
    public void ASessionThatGivesNoFirstFrameFallsBackToPrintWindow()
    {
        using var h = new Harness(start: _ => new FakeSession()); // no frames ever

        var shot = h.Take();

        Assert.Equal(PictureSource.PrintWindow, shot.Source);
        Assert.NotNull(shot.Value);
        Assert.Contains(h.Reports, r => r.Kind == "wgc-failed" && r.Message.Contains("no first frame", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailureWhileReadingDropsTheSessionAndFallsBack()
    {
        var made = new List<FakeSession>();
        using var h = new Harness(start: _ =>
        {
            var s = new FakeSession { ReadFailure = new InvalidOperationException("device removed") };
            s.Frames.Add(new FakeFrame(10, 8));
            made.Add(s);
            return s;
        });

        var shot = h.Take();

        Assert.Equal(PictureSource.PrintWindow, shot.Source);
        Assert.True(made[0].Disposed);
        Assert.Equal(0, h.Capture.OpenSessions);
        Assert.Contains(h.Reports, r => r.Kind == "wgc-failed");
    }

    [Fact]
    public void AFrameOfTheWrongSizeIsReadAgainFromANewerFrame()
    {
        // The window is being resized: the frame in hand is the old size, the next one is right.
        var made = new List<FakeSession>();
        using var h = new Harness(start: _ =>
        {
            var s = new FakeSession();
            s.Frames.Add(new FakeFrame(20, 8)); // the old size
            s.Frames.Add(new FakeFrame(10, 8));
            made.Add(s);
            return s;
        });

        var shot = h.Take();

        Assert.Equal(PictureSource.GraphicsCapture, shot.Source);
        Assert.Equal((6, 5), (shot.Value!.Value.W, shot.Value.Value.H));
        Assert.Equal(
            [(WindowPictureCapture.FirstFrameTimeout, false), (WindowPictureCapture.ResizeTimeout, true)],
            made[0].Reads);
        Assert.Equal(0, h.Print.Calls);
    }

    [Fact]
    public void AFrameThatStaysTheWrongSizeGivesNoPictureAndNoFallback()
    {
        using var h = new Harness(start: _ =>
        {
            var s = new FakeSession();
            s.Frames.Add(new FakeFrame(20, 8)); // never the size the window has
            return s;
        });

        var shot = h.Take();

        Assert.Equal(PictureSource.None, shot.Source);
        Assert.Null(shot.Value);
        Assert.Equal(0, h.Print.Calls); // not PrintWindow: the window is fine, only its frame is not
        Assert.Contains(h.Reports, r => r.Kind == "geometry" && r.Message.Contains("20x8", StringComparison.Ordinal));
        Assert.Equal(0, h.Capture.OpenSessions); // the session is closed: the next shot starts one at the window's size
    }

    [Fact]
    public void AWindowWhoseFrameCannotBeMeasuredIsPrinted()
    {
        using var h = new Harness();
        h.Facts.Geometry = null;

        var shot = h.Take();

        Assert.Equal(PictureSource.PrintWindow, shot.Source);
        Assert.Empty(h.StartedFor);
    }

    [Fact]
    public void ASessionIsKeptForTheNextShotAndClosedWhenAnotherWindowIsAskedFor()
    {
        var sessions = new List<(nint Root, FakeSession Session)>();
        using var h = new Harness(start: root =>
        {
            var s = new FakeSession();
            s.Frames.Add(new FakeFrame(10, 8));
            sessions.Add((root, s));
            return s;
        });

        _ = h.Take();
        _ = h.Take();
        Assert.Single(sessions); // kept
        Assert.Equal(1, h.Capture.OpenSessions);

        h.Facts.Geometry = new WindowGeometry(Root + 1, new RectI(100, 100, 10, 8), new RectI(102, 101, 6, 5)); // the receiver's window changed
        _ = h.Take();

        Assert.Equal(2, sessions.Count);
        Assert.True(sessions[0].Session.Disposed);
        Assert.False(sessions[1].Session.Disposed);
        Assert.Equal(1, h.Capture.OpenSessions);
    }

    [Fact]
    public void ASessionIsClosedAfterTheIdleTimeWithoutUseAndDisposeClosesTheRest()
    {
        using var h = new Harness();
        _ = h.Take();
        var session = h.Sessions[0];

        h.Time.Advance(CaptureSessionCache<IWgcSession>.IdleTime - TimeSpan.FromSeconds(0.1));
        Assert.False(session.Disposed);
        h.Time.Advance(TimeSpan.FromSeconds(0.2)); // 0.1 s past the idle time
        Assert.True(session.Disposed);
        Assert.Equal(0, h.Capture.OpenSessions);

        _ = h.Take(); // a new session is started on the next shot
        Assert.Equal(2, h.Sessions.Count);
        h.Capture.Dispose();
        Assert.True(h.Sessions[1].Disposed);
        Assert.Throws<ObjectDisposedException>(() => h.Take());
    }

    private static FakeSession WrongSizeSession()
    {
        var s = new FakeSession();
        s.Frames.Add(new FakeFrame(20, 8)); // never the size the window has
        return s;
    }

    [Fact]
    public void ThreeMismatchesInARowForAWindowThatDidNotChangeAreHowTheSystemIsAndFallBack()
    {
        var starts = 0;
        using var h = new Harness(start: _ =>
        {
            starts++;
            return WrongSizeSession();
        });

        var first = h.Take();
        var second = h.Take();
        Assert.Equal(PictureSource.None, first.Source); // a resize may be on its way: no picture, no fallback
        Assert.Equal(PictureSource.None, second.Source);
        Assert.Equal(0, h.Print.Calls);
        Assert.DoesNotContain(h.Reports, r => r.Kind == "wgc-failed");

        var third = h.Take();

        Assert.Equal(PictureSource.PrintWindow, third.Source);
        Assert.NotNull(third.Value);
        Assert.Equal(1, h.Print.Calls);
        Assert.Single(h.Reports, r => r.Kind == "wgc-failed");

        // The back-off of a failure: PrintWindow without trying a session, for 30 seconds.
        Assert.Equal(PictureSource.PrintWindow, h.Take().Source);
        Assert.Equal(3, starts);
        Assert.Single(h.Reports, r => r.Kind == "wgc-failed"); // reported once

        h.Time.Advance(WindowPictureCapture.RetryAfterFailure);
        Assert.Equal(PictureSource.None, h.Take().Source); // tried again, and the count starts afresh
        Assert.Equal(4, starts);
    }

    [Fact]
    public void AGoodShotStartsTheCountOfMismatchesAfresh()
    {
        var good = false;
        using var h = new Harness(start: _ =>
        {
            var s = new FakeSession();
            s.Frames.Add(good ? new FakeFrame(10, 8) : new FakeFrame(20, 8));
            return s;
        });

        _ = h.Take();
        _ = h.Take();
        good = true;
        Assert.Equal(PictureSource.GraphicsCapture, h.Take().Source);
        good = false;
        h.Time.Advance(CaptureSessionCache<IWgcSession>.IdleTime); // the kept session is closed: the next shot starts a new one
        Assert.Equal(PictureSource.None, h.Take().Source);
        Assert.Equal(PictureSource.None, h.Take().Source); // two again, not the third

        Assert.Equal(0, h.Print.Calls);
        Assert.DoesNotContain(h.Reports, r => r.Kind == "wgc-failed");
    }

    [Fact]
    public void MismatchesWhileTheWindowChangesAreAResizeAndNeverFallBack()
    {
        using var h = new Harness(start: _ => WrongSizeSession());

        for (var i = 0; i < 6; i++)
        {
            // The window moves its client area on every shot (a resize is on its way), so no two shots see the same window.
            h.Facts.Geometry = new WindowGeometry(Root, new RectI(100, 100, 10, 8), new RectI(102 + (i % 2), 101, 6, 5));
            Assert.Equal(PictureSource.None, h.Take().Source);
        }

        Assert.Equal(0, h.Print.Calls);
        Assert.DoesNotContain(h.Reports, r => r.Kind == "wgc-failed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnExceptionOfTheReaderGoesOutAsItIsAndIsNoFailureOfTheCapture(int kind)
    {
        using var h = new Harness();
        _ = h.Take(); // a session is open
        var reads = 0;
        Exception Failure() => kind switch
        {
            0 => new InvalidOperationException("reader"),
            1 => new System.Runtime.InteropServices.COMException("reader", unchecked((int)0x80004005)),
            2 => new OutOfMemoryException("reader"),
            _ => new UnauthorizedAccessException("reader"),
        };

        var thrown = Assert.ThrowsAny<Exception>(() => h.Capture.Capture<int>(Window, (_, _, _, _) =>
        {
            reads++;
            throw Failure();
        }));

        Assert.Equal("reader", thrown.Message);
        Assert.Equal(1, reads); // not run again on PrintWindow
        Assert.Equal(0, h.Print.Calls);
        Assert.DoesNotContain(h.Reports, r => r.Kind is "wgc-failed" or "wgc-denied");
        Assert.Single(h.Sessions); // no new session, no new device
        Assert.False(h.Sessions[0].Disposed);

        // No back-off: the next shot is a normal one.
        Assert.Equal(PictureSource.GraphicsCapture, h.Take().Source);
        Assert.Single(h.Sessions);
        Assert.Equal(0, h.Print.Calls);
    }

    [Fact]
    public void AnExceptionOfTheReaderOnAMismatchRetryGoesOutToo()
    {
        var made = new List<FakeSession>();
        using var h = new Harness(start: _ =>
        {
            var s = new FakeSession();
            s.Frames.Add(new FakeFrame(20, 8)); // the old size
            s.Frames.Add(new FakeFrame(10, 8));
            made.Add(s);
            return s;
        });

        Assert.Throws<InvalidOperationException>(() => h.Capture.Capture<int>(Window, (_, _, _, _) => throw new InvalidOperationException("reader")));

        Assert.Equal(0, h.Print.Calls);
        Assert.Empty(h.Reports);
    }

    [Fact]
    public void AWindowThatForbidsBeingCapturedGivesNoPictureAndIsNotPrinted()
    {
        var starts = 0;
        using var h = new Harness(start: _ =>
        {
            starts++;
            throw new UnauthorizedAccessException("access denied");
        });

        var first = h.Take();
        var second = h.Take();

        Assert.Equal(PictureSource.None, first.Source);
        Assert.Null(first.Value);
        Assert.Equal(PictureSource.None, second.Source);
        Assert.Equal(0, h.Print.Calls); // the refusal is not got round
        Assert.Equal(2, starts); // no back-off: Windows is asked again (the user may allow it)
        Assert.Equal(2, h.Reports.Count(r => r.Kind == "wgc-denied" && r.Message.Contains("access denied", StringComparison.Ordinal)));
        Assert.DoesNotContain(h.Reports, r => r.Kind == "wgc-failed");
    }

    [Fact]
    public void AWindowThatGoesAwayBeforeItsSessionStartsIsNoFailureAndHasNoBackOff()
    {
        var starts = 0;
        Harness h = null!;
        h = new Harness(start: _ =>
        {
            starts++;
            h.Facts.Presence = WindowPresence.Hidden; // gone between the check and the session
            throw new ArgumentException("invalid window");
        });
        using var holder = h;

        var shot = h.Take();

        Assert.Equal(PictureSource.None, shot.Source);
        Assert.Equal(0, h.Print.Calls);
        Assert.DoesNotContain(h.Reports, r => r.Kind == "wgc-failed");

        h.Facts.Presence = WindowPresence.Shown; // it is back: the next shot tries at once
        _ = h.Take();
        Assert.Equal(2, starts);
    }

    [Fact]
    public void AnArgumentErrorForAWindowThatIsStillThereIsAFailure()
    {
        using var h = new Harness(start: _ => throw new ArgumentException("bad argument"));

        var shot = h.Take();

        Assert.Equal(PictureSource.PrintWindow, shot.Source);
        Assert.Single(h.Reports, r => r.Kind == "wgc-failed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NoMemoryOrNotImplementedFromWindowsIsAFailureOfTheCaptureAndFallsBack(int kind)
    {
        using var h = new Harness(start: _ => throw (kind == 0 ? new OutOfMemoryException("E_OUTOFMEMORY") : new NotImplementedException("E_NOTIMPL")));

        var shot = h.Take();

        Assert.Equal(PictureSource.PrintWindow, shot.Source);
        Assert.Single(h.Reports, r => r.Kind == "wgc-failed");
    }
}

public sealed class CaptureSessionCacheTests
{
    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }

        public Exception? ThrowOnDispose { get; init; }

        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose is not null)
            {
                throw ThrowOnDispose;
            }
        }
    }

    private static (CaptureSessionCache<Probe> Cache, FakeTimeProvider Time, List<Probe> Made) Make()
    {
        var time = new FakeTimeProvider();
        List<Probe> made = [];
        var cache = new CaptureSessionCache<Probe>(time, _ =>
        {
            var p = new Probe();
            made.Add(p);
            return p;
        });
        return (cache, time, made);
    }

    [Fact]
    public void TheIdleTimeIsTwoSeconds()
    {
        // Windows shows a border around the window while a session is open: it must not stay long after the last screenshot.
        Assert.Equal(TimeSpan.FromSeconds(2), CaptureSessionCache<Probe>.IdleTime);
    }

    [Fact]
    public void AUseKeepsTheSessionAndEachUseRestartsTheIdleTime()
    {
        var (cache, time, made) = Make();

        cache.Use(1, p => p);
        time.Advance(CaptureSessionCache<Probe>.IdleTime - TimeSpan.FromSeconds(0.1));
        cache.Use(1, p => p);
        time.Advance(CaptureSessionCache<Probe>.IdleTime - TimeSpan.FromSeconds(0.1));

        Assert.Single(made);
        Assert.False(made[0].Disposed); // 0.1 s short of the idle time since the last use
        time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(made[0].Disposed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ASweepClosesOnlyTheIdleOnes()
    {
        var (cache, time, made) = Make();
        cache.Use(1, p => p);
        time.Advance(CaptureSessionCache<Probe>.IdleTime); // the timer closes the first (it is the only one)
        Assert.True(made[0].Disposed);

        cache.Use(2, p => p);
        time.Advance(CaptureSessionCache<Probe>.IdleTime / 2);
        cache.Sweep();

        Assert.False(made[1].Disposed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AnotherKeyClosesTheSessionsOfTheOthers()
    {
        var (cache, _, made) = Make();

        cache.Use(1, p => p);
        cache.Use(2, p => p);

        Assert.True(made[0].Disposed);
        Assert.False(made[1].Disposed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AUseThatThrowsClosesItsSessionAndTheNextUseStartsANewOne()
    {
        var (cache, _, made) = Make();

        Assert.Throws<InvalidOperationException>(() => cache.Use<int>(1, _ => throw new InvalidOperationException("lost device")));
        Assert.True(made[0].Disposed);
        Assert.Equal(0, cache.Count);

        cache.Use(1, p => p);
        Assert.Equal(2, made.Count);
    }

    [Fact]
    public void AFailingCreateLeavesNothingBehind()
    {
        var cache = new CaptureSessionCache<Probe>(new FakeTimeProvider(), _ => throw new InvalidOperationException("no device"));

        Assert.Throws<InvalidOperationException>(() => cache.Use(1, p => p));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DisposeClosesEverythingAndLaterUsesFail()
    {
        var (cache, _, made) = Make();
        cache.Use(1, p => p);

        cache.Dispose();

        Assert.True(made[0].Disposed);
        Assert.Throws<ObjectDisposedException>(() => cache.Use(1, p => p));
        cache.Dispose(); // twice is fine
    }

    [Fact]
    public async Task ASessionThatIsBeingUsedIsNotSweptAway()
    {
        var (cache, time, made) = Make();
        using var inUse = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var use = Task.Run(() => cache.Use(1, p =>
        {
            inUse.Set();
            release.Wait();
            return p;
        }));
        Assert.True(inUse.Wait(TimeSpan.FromSeconds(10)));

        time.Advance(TimeSpan.FromSeconds(30));
        cache.Sweep(); // from this thread, while the other one is inside the use: skipped, not blocked

        Assert.False(made[0].Disposed);
        release.Set();
        await use.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(made[0].Disposed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void WhenTheLastSessionIsGoneTheCacheSaysSoOnceForEveryWayOfGoingAway()
    {
        var time = new FakeTimeProvider();
        var empty = 0;
        var cache = new CaptureSessionCache<Probe>(time, _ => new Probe(), onEmpty: () => empty++);

        cache.Use(1, p => p);
        cache.Use(2, p => p); // another window: the first closes, but there is a session all along
        Assert.Equal(0, empty);

        time.Advance(CaptureSessionCache<Probe>.IdleTime); // the sweep
        Assert.Equal(1, empty);

        cache.Use(1, p => p);
        cache.Drop(1);
        Assert.Equal(2, empty);

        cache.Use(1, p => p);
        cache.Clear();
        Assert.Equal(3, empty);

        Assert.Throws<InvalidOperationException>(() => cache.Use<int>(1, _ => throw new InvalidOperationException("lost")));
        Assert.Equal(4, empty);
    }

    [Fact]
    public void AnOnEmptyThatThrowsDoesNotBreakTheCache()
    {
        var cache = new CaptureSessionCache<Probe>(new FakeTimeProvider(), _ => new Probe(), onEmpty: () => throw new InvalidOperationException("no device"));
        cache.Use(1, p => p);

        cache.Drop(1);

        Assert.Equal(0, cache.Count);
        cache.Use(1, p => p);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void ClearRunsItsActionAfterTheSessionsAreClosedAndBeforeAnotherUseCanStartOne()
    {
        var (cache, _, made) = Make();
        cache.Use(1, p => p);
        (bool Closed, int Count) seen = default;

        cache.Clear(() => seen = (made[0].Disposed, cache.Count));

        Assert.Equal((true, 0), seen);
    }

    [Fact]
    public void ASessionThatThrowsWhenItIsClosedLeavesTheCacheAndTheOtherSessionsAlone()
    {
        var made = new List<Probe>();
        var cache = new CaptureSessionCache<Probe>(new FakeTimeProvider(), _ =>
        {
            var p = new Probe { ThrowOnDispose = new Exception("half closed") };
            made.Add(p);
            return p;
        });

        // The use fails and the dispose of its session fails too: the first failure is the one that goes out.
        var thrown = Assert.Throws<InvalidOperationException>(() => cache.Use<int>(1, _ => throw new InvalidOperationException("lost device")));
        Assert.Equal("lost device", thrown.Message);
        Assert.Equal(0, cache.Count);

        cache.Use(1, p => p);
        cache.Clear(); // does not throw
        cache.Use(1, p => p);
        cache.Use(2, p => p); // closing the other one throws inside; the new one is made all the same
        Assert.Equal(1, cache.Count);
        cache.Dispose();
        Assert.All(made, p => Assert.True(p.Disposed));
    }
}
