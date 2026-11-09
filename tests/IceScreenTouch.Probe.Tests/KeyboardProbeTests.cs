// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using System.Text.Json;
using IceScreenTouch.Core;
using IceScreenTouch.Probe;

namespace IceScreenTouch.Probe.Tests;

public sealed class KeyboardProbeTests
{
    [Fact]
    public void PredictsWhatTheOtherLayoutTypes()
    {
        // US keys for @ " : = are JIS " * + ^ (Shift+2, Shift+', Shift+;, =)
        Assert.Equal("\"*+^", KeyboardProbe.Predict("@\":=", HidKeyboardLayout.Us, HidKeyboardLayout.Jis));
        Assert.Equal("@\":=", KeyboardProbe.Predict("@\":=", HidKeyboardLayout.Us, HidKeyboardLayout.Us));
        Assert.Equal("abc 123", KeyboardProbe.Predict("abc 123", HidKeyboardLayout.Jis, HidKeyboardLayout.Us));
    }

    [Fact]
    public void TextHelpers()
    {
        Assert.Equal("xyz", KeyboardProbe.Typed("ab", "abxyz"));
        Assert.Equal("q", KeyboardProbe.Typed("ab", "q"));
        Assert.Equal("'\"'\"\n", KeyboardProbe.Normalize("‘“’”\r\n"));
        Assert.True(KeyboardProbe.IsJapanese("にほんご"));
        Assert.True(KeyboardProbe.IsJapanese("ａｂｃ"));
        Assert.False(KeyboardProbe.IsJapanese("abc ¥"));
        Assert.Equal(0, KeyboardProbe.Distance("abc", "abc"));
        Assert.Equal(1, KeyboardProbe.Distance("abc", "abd"));
        Assert.Equal(3, KeyboardProbe.Distance("", "abc"));
    }

    [Fact]
    public async Task PassesOnADeviceWithTheSameLayout()
    {
        var (result, device) = await RunAsync(HidKeyboardLayout.Us, HidKeyboardLayout.Us);

        Assert.True(result.Passed, string.Join("; ", result.Checks.Select(c => $"{c.Name}: {c.Ok} {c.Detail}")));
        Assert.Equal(6, result.Checks.Count);
        Assert.NotNull(result.LatencyMs);
        Assert.Null(result.LikelyLayout);
        Assert.Equal(string.Empty, device.Text); // the box is left empty
        Assert.Contains("11 of 11", result.Checks[0].Detail, StringComparison.Ordinal); // the Shift keydowns are not keys
    }

    [Fact]
    public async Task ASpaceThatIOSAddsWhenPastingIsFine()
    {
        var (result, _) = await RunAsync(HidKeyboardLayout.Us, HidKeyboardLayout.Us, smartPaste: true);

        Assert.True(result.Passed, string.Join("; ", result.Checks.Select(c => $"{c.Name}: {c.Ok} {c.Detail}")));
        Assert.Contains("iOS added a space", result.Checks.Single(c => c.Name == KeyboardProbe.PasteCheck).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeviceSetToTheOtherLayoutIsNamed()
    {
        var (result, _) = await RunAsync(HidKeyboardLayout.Us, HidKeyboardLayout.Jis);

        Assert.False(result.Passed);
        Assert.False(result.Checks.Single(c => c.Name == KeyboardProbe.SymbolsCheck).Ok);
        Assert.True(result.Checks.Single(c => c.Name == KeyboardProbe.LettersCheck).Ok);
        Assert.Equal(HidKeyboardLayout.Jis, result.LikelyLayout);

        var (back, _) = await RunAsync(HidKeyboardLayout.Jis, HidKeyboardLayout.Us);
        Assert.Equal(HidKeyboardLayout.Us, back.LikelyLayout);
    }

    [Fact]
    public async Task JapaneseInputIsReported()
    {
        var (result, _) = await RunAsync(HidKeyboardLayout.Us, HidKeyboardLayout.Us, japanese: true);

        Assert.False(result.Passed);
        Assert.True(result.JapaneseInput);
        Assert.Null(result.LikelyLayout);
    }

    [Fact]
    public async Task SpaceAndArrowsTakenByTheDeviceAreReported()
    {
        // Full Keyboard Access on an iPad: no keydown for Space or the arrows, everything else as usual (2026-11-06).
        var (result, _) = await RunAsync(HidKeyboardLayout.Us, HidKeyboardLayout.Us, navigationTaken: true);

        Assert.False(result.Passed);
        Assert.True(result.NavigationKeysTaken);
        Assert.False(result.JapaneseInput);
        Assert.Contains("9 of 11", result.Checks[0].Detail, StringComparison.Ordinal);
        Assert.True(result.Checks.Single(c => c.Name == KeyboardProbe.SymbolsCheck).Ok);

        var (fine, _) = await RunAsync(HidKeyboardLayout.Us, HidKeyboardLayout.Us);
        Assert.False(fine.NavigationKeysTaken);
        var (japanese, _) = await RunAsync(HidKeyboardLayout.Us, HidKeyboardLayout.Us, japanese: true);
        Assert.False(japanese.NavigationKeysTaken);
    }

    [Fact]
    public async Task NothingArrivingStopsEarly()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        server.Add("""{"type":"hello","pg":"keys","state":"visible","f":true,"v":""}""");
        var sink = new SilentSink();
        var result = await new KeyboardProbe(sink, server, TextWriter.Null) { Quiet = TimeSpan.FromMilliseconds(30), StepTimeout = TimeSpan.FromMilliseconds(200) }
            .RunAsync(HidKeyboardLayout.Us);

        Assert.True(result.NothingArrived);
        Assert.Single(result.Checks);
    }

    [Fact]
    public async Task ThePageLeavingTheFrontStopsTheProbe()
    {
        // The page went to the background (another app came to the front): the editing keys must not follow.
        var server = new ProbeServer(0, IPAddress.Loopback);
        server.Add("""{"type":"hello","pg":"keys","state":"hidden","f":true,"v":""}""");
        var device = new FakeDevice(server, HidKeyboardLayout.Us, false, false, false);
        var output = new StringWriter();

        await Assert.ThrowsAsync<ProbePageLeftException>(() =>
            new KeyboardProbe(device, server, output) { Quiet = TimeSpan.FromMilliseconds(60), StepTimeout = TimeSpan.FromSeconds(2) }
                .RunAsync(HidKeyboardLayout.Us));

        Assert.Contains("no longer in front", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsTypedWhenThePageIsNotInFrontAtTheStart()
    {
        // The phone switched apps during the countdown: not even the letters may go out (review, 2026-11-07).
        var server = new ProbeServer(0, IPAddress.Loopback);
        server.Add("""{"type":"hello","pg":"keys","state":"hidden","f":true,"v":""}""");
        var sink = new CountingSink();
        var output = new StringWriter();

        await Assert.ThrowsAsync<ProbePageLeftException>(() =>
            new KeyboardProbe(sink, server, output) { Quiet = TimeSpan.FromMilliseconds(60), StepTimeout = TimeSpan.FromSeconds(2) }
                .RunAsync(HidKeyboardLayout.Us));

        Assert.Equal(0, sink.Reports);
        Assert.Contains("no longer in front", output.ToString(), StringComparison.Ordinal);
    }

    private static async Task<(KeyboardProbeResult Result, FakeDevice Device)> RunAsync(
        HidKeyboardLayout assumed, HidKeyboardLayout actual, bool japanese = false, bool smartPaste = false, bool navigationTaken = false)
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        server.Add("""{"type":"hello","pg":"keys","state":"visible","f":true,"v":""}""");
        var device = new FakeDevice(server, actual, japanese, smartPaste, navigationTaken);
        var result = await new KeyboardProbe(device, server, TextWriter.Null) { Quiet = TimeSpan.FromMilliseconds(60), StepTimeout = TimeSpan.FromSeconds(2) }
            .RunAsync(assumed);
        return (result, device);
    }

    private sealed class CountingSink : IKeyboardSink
    {
        public int Reports { get; private set; }

        public bool HasKeyboard => true;

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            Reports++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SilentSink : IKeyboardSink
    {
        public bool HasKeyboard => true;

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>A text box on a device with a hardware keyboard: turns key-down reports into the page's events.</summary>
    private sealed class FakeDevice(ProbeServer server, HidKeyboardLayout layout, bool japanese, bool smartPaste, bool navigationTaken) : IKeyboardSink
    {
        private readonly StringBuilder _text = new();
        private int _start;
        private int _end;
        private string _clipboard = string.Empty;
        private byte _lastKey;

        public bool HasKeyboard => true;

        public string Text => _text.ToString();

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            RepeatHello();
            var key = keys.Length > 0 ? keys.Span[^1] : (byte)0;
            if (key == 0 && _lastKey == 0 && (modifiers & HidModifiers.LeftShift) != 0)
            {
                Post("keydown", "Shift"); // Safari reports a modifier pressed alone
            }

            if (key != 0 && key != _lastKey)
            {
                Press(new HidKey(key, modifiers));
            }

            _lastKey = key;
            return ValueTask.CompletedTask;
        }

        // The real page says hello every 2 s while it is in front (ProbeReadiness.KeysPageFreshSeconds is 6 s). A full
        // probe run takes about 5 s on an idle machine, so a single hello at the start goes stale on a slow runner and
        // the probe rightly stops with "no longer in front". Repeat it like the page does, while the page is visible.
        private void RepeatHello()
        {
            var hello = server.HelloFor(ProbePage.Keys);
            if (hello is { State: "visible" } && ProbeClock.Now() - hello.T >= 1.0)
            {
                server.Add(JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = "hello", ["pg"] = "keys", ["state"] = "visible", ["f"] = true, ["v"] = Text }));
            }
        }

        private void Press(HidKey key)
        {
            var command = (key.Modifiers & HidModifiers.LeftGui) != 0;
            if (navigationTaken && key.Usage is 0x2C or HidKeyboard.LeftArrow or HidKeyboard.RightArrow)
            {
                return; // the device keeps the key for itself: the page sees nothing
            }

            Post("keydown", "k");
            switch (key.Usage)
            {
                case 0x04 when command:
                    (_start, _end) = (0, _text.Length);
                    Post("sel");
                    return;
                case 0x06 when command:
                    _clipboard = Text[_start.._end];
                    return;
                case 0x19 when command:
                    Insert(smartPaste && _start > 0 && _text[_start - 1] != ' ' ? " " + _clipboard : _clipboard);
                    return;
                case HidKeyboard.Enter:
                    Insert("\n");
                    return;
                case HidKeyboard.Backspace:
                    if (_start == _end && _start > 0)
                    {
                        _start--;
                    }

                    _text.Remove(_start, _end - _start);
                    _end = _start;
                    Post("input");
                    return;
                case HidKeyboard.LeftArrow:
                    _start = _end = _start == _end ? Math.Max(0, _start - 1) : _start;
                    Post("sel");
                    return;
                case HidKeyboard.RightArrow:
                    _start = _end = _start == _end ? Math.Min(_text.Length, _end + 1) : _end;
                    Post("sel");
                    return;
            }

            if (HidKeyboard.TryChar(key, layout, out var c))
            {
                Insert(japanese && char.IsAsciiLetter(c) ? "あ" : c.ToString());
            }
        }

        private void Insert(string s)
        {
            _text.Remove(_start, _end - _start).Insert(_start, s);
            _start = _end = _start + s.Length;
            Post("input");
        }

        private void Post(string type, string? key = null) =>
            server.Add(JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = type, ["key"] = key, ["v"] = Text, ["ss"] = _start, ["se"] = _end }));
    }
}
