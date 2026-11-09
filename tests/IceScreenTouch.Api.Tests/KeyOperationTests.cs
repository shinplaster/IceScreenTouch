// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

public class KeyOperationTests : ApiTestBase
{
    public static TheoryData<string, string[], byte, byte> ValidKeys => new()
    {
        { "Space", ["ctrl"], HidKeyboard.Space, HidModifiers.LeftControl },
        { "a", ["cmd"], 0x04, HidModifiers.LeftGui },
        { "Enter", [], HidKeyboard.Enter, 0 },
        { "enter", [], HidKeyboard.Enter, 0 },
        { "Escape", [], HidKeyboard.Escape, 0 },
        { "Backspace", [], HidKeyboard.Backspace, 0 },
        { "Tab", ["shift"], HidKeyboard.Tab, HidModifiers.LeftShift },
        { "Delete", [], HidKeyboard.Delete, 0 },
        { "ArrowUp", [], HidKeyboard.UpArrow, 0 },
        { "ArrowDown", [], HidKeyboard.DownArrow, 0 },
        { "ArrowLeft", [], HidKeyboard.LeftArrow, 0 },
        { "ArrowRight", [], HidKeyboard.RightArrow, 0 },
        { "PageUp", [], 0x4B, 0 },
        { "PageDown", [], 0x4E, 0 },
        { "Home", [], 0x4A, 0 },
        { "End", [], 0x4D, 0 },
        { "F1", [], 0x3A, 0 },
        { "F12", [], 0x45, 0 },
        { "z", ["cmd", "shift"], 0x1D, HidModifiers.LeftGui | HidModifiers.LeftShift },
        { "0", [], 0x27, 0 },
        { "1", [], 0x1E, 0 },
        { "-", [], 0x2D, 0 },
        { "=", [], 0x2E, 0 },
        { "[", [], 0x2F, 0 },
        { "]", [], 0x30, 0 },
        { "\\", [], 0x31, 0 },
        { ";", [], 0x33, 0 },
        { "'", [], 0x34, 0 },
        { ",", [], 0x36, 0 },
        { ".", [], 0x37, 0 },
        { "/", [], 0x38, 0 },
        { "`", [], 0x35, 0 },
        { "c", ["Cmd", "OPTION", "Ctrl"], 0x06, HidModifiers.LeftGui | HidModifiers.LeftAlt | HidModifiers.LeftControl },
    };

    [Theory]
    [MemberData(nameof(ValidKeys))]
    public void KeyNamesMapToHidKeys(string name, string[] modifiers, byte usage, byte bits)
    {
        Assert.True(ApiKeys.TryParse(name, modifiers, out var key, out _));
        Assert.Equal(new HidKey(usage, bits), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")] // needs Shift on its own: send "a" with the shift modifier
    [InlineData("!")]
    [InlineData("kana")]
    [InlineData("0x90")]
    [InlineData("F13")]
    [InlineData("up")]
    [InlineData("foo")]
    [InlineData("ほ")]
    public void UnknownKeysAreRefused(string name)
    {
        Assert.False(ApiKeys.TryParse(name, null, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("alt")]
    [InlineData("win")]
    [InlineData("")]
    public void UnknownModifiersAreRefused(string modifier)
    {
        Assert.False(ApiKeys.TryParse("a", [modifier], out _, out _));
    }

    [Fact]
    public async Task CtrlSpaceSendsTheModifierThenTheKeyThenReleasesEverything()
    {
        var result = await Time.Drive(Controller.PressKeyAsync(new KeyPressRequest { Key = "Space", Modifiers = ["ctrl"] }));

        Assert.True(result.IsSuccess);
        var reports = Keys.Reports;
        Assert.Equal(HidModifiers.LeftControl, reports[0].Modifiers); // the modifier alone first
        Assert.Empty(reports[0].Keys);
        Assert.Equal(HidModifiers.LeftControl, reports[1].Modifiers);
        Assert.Equal([HidKeyboard.Space], reports[1].Keys);
        Assert.Equal(Ms(30), reports[2].At - reports[1].At); // default hold
        Assert.True(reports[^1].IsRelease);
    }

    [Fact]
    public async Task CmdASendsGuiPlusA()
    {
        await Time.Drive(Controller.PressKeyAsync(new KeyPressRequest { Key = "a", Modifiers = ["cmd"], HoldMs = 100 }));

        var down = Keys.Reports.Single(r => r.Keys.Length > 0);
        Assert.Equal(HidModifiers.LeftGui, down.Modifiers);
        Assert.Equal([(byte)0x04], down.Keys);
        var after = Keys.Reports[Keys.Reports.IndexOf(down) + 1];
        Assert.Equal(Ms(100), after.At - down.At);
    }

    [Fact]
    public async Task KeyHoldIsNotRaisedToSeventyMilliseconds()
    {
        await Time.Drive(Controller.PressKeyAsync(new KeyPressRequest { Key = "b", HoldMs = 5 }));

        Assert.Equal(Ms(5), Keys.Reports[1].At - Keys.Reports[0].At);
    }

    [Fact]
    public async Task UnknownKeyOrModifierIsInvalidAndSendsNothing()
    {
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = "NoSuchKey" }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = "a", Modifiers = ["hyper"] }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = null! }), ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
        Assert.Empty(Host.Leases);
    }

    [Fact]
    public async Task TypeSendsOneKeyPerCharacterWithTheInterval()
    {
        var result = await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = "hi" }));

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Value!.Skipped);
        var downs = Keys.Reports.Where(r => r.Keys.Length > 0).ToList();
        Assert.Equal([(byte)0x0B, (byte)0x0C], downs.Select(d => d.Keys.Single()));
        Assert.Equal(Ms(30 + 20), downs[1].At - downs[0].At); // hold 30 + default interval 20
        Assert.True(Keys.Reports[^1].IsRelease);
    }

    [Fact]
    public async Task TypeReturnsTheCharactersThatHaveNoKey()
    {
        var result = await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = "hi ほん😀!" }));

        Assert.True(result.IsSuccess);
        Assert.Equal("ほん😀".Length, result.Value!.Skipped.Length);
        Assert.Equal("ほん😀", result.Value.Skipped);
        // h, i, space and '!' (shift + 1) were typed.
        Assert.Equal(4, Keys.Reports.Count(r => r.Keys.Length > 0 && r.Keys[0] is 0x0B or 0x0C or 0x2C or 0x1E));
    }

    [Fact]
    public async Task TypeUsesTheUsLayoutAndTurnsNewlinesIntoEnter()
    {
        await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = "A@\r\n", IntervalMs = 0 }));

        var downs = Keys.Reports.Where(r => r.Keys.Length > 0).ToList();
        Assert.Equal(3, downs.Count);
        Assert.Equal((HidModifiers.LeftShift, (byte)0x04), (downs[0].Modifiers, downs[0].Keys[0])); // A
        Assert.Equal((HidModifiers.LeftShift, (byte)0x1F), (downs[1].Modifiers, downs[1].Keys[0])); // @ is shift + 2 on US
        Assert.Equal((byte)0, downs[2].Modifiers);
        Assert.Equal(HidKeyboard.Enter, downs[2].Keys[0]);
    }

    [Fact]
    public async Task TypeLimits()
    {
        AssertError(await Controller.TypeTextAsync(new TypeTextRequest { Text = new string('a', ApiController.MaxTextLength + 1) }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TypeTextAsync(new TypeTextRequest { Text = "a", IntervalMs = -1 }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TypeTextAsync(new TypeTextRequest { Text = "a", IntervalMs = ApiController.MaxTypeIntervalMs + 1 }), ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task ThousandCharactersAtTheDefaultIntervalFitInSixtySeconds()
    {
        var result = await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = new string('a', 1000) }));

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, Keys.Reports.Count(r => r.Keys.Length > 0));
        Assert.Equal(Ms((999 * 50) + 30), Keys.Reports[^1].At); // the last release; 30 ms hold + 20 ms interval each before it
    }

    [Fact]
    public async Task AThousandAndOneCharactersAreRefused()
    {
        var result = await Controller.TypeTextAsync(new TypeTextRequest { Text = new string('a', 1001), IntervalMs = 0 });

        AssertError(result, ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
        Assert.Empty(Host.Leases);
    }

    [Fact]
    public async Task TypingThatWouldTakeMoreThanSixtySecondsIsRefusedBeforeAnythingIsSent()
    {
        // Shifted characters cost 30 + 20 + 30 ms: 1000 of them take 80 s.
        var shifted = await Controller.TypeTextAsync(new TypeTextRequest { Text = new string('A', 1000) });
        // 1000 plain characters with a 31 ms interval take 61 s.
        var slow = await Controller.TypeTextAsync(new TypeTextRequest { Text = new string('a', 1000), IntervalMs = 31 });

        AssertError(shifted, ApiErrorCodes.InvalidRequest);
        Assert.Contains("split", shifted.Error!.Detail, StringComparison.Ordinal);
        AssertError(slow, ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
        Assert.Empty(Host.Leases);
    }

    [Fact]
    public async Task ExactlySixtySecondsOfTypingIsAllowed()
    {
        var result = await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = new string('a', 1000), IntervalMs = 30 }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Ms((999 * 60) + 30), Keys.Reports[^1].At); // the last release; the pause after it is the 1000th interval
    }

    [Fact]
    public async Task SkippedCharactersDoNotCountTowardsTheLimit()
    {
        // 600 shifted letters take 48 s; the 400 characters without a key cost nothing.
        var text = new string('A', 600) + new string('ほ', 400);

        var result = await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = text }));

        Assert.True(result.IsSuccess);
        Assert.Equal(400, result.Value!.Skipped.Length);

        var onlySkipped = await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = new string('ほ', 1000), IntervalMs = 1000 }));
        Assert.True(onlySkipped.IsSuccess);
        Assert.Equal(1000, onlySkipped.Value!.Skipped.Length);
    }

    [Fact]
    public async Task TypeOfEmptyTextSucceedsAndSendsNothing()
    {
        var result = await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = string.Empty }));

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Value!.Skipped);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task HomePressesTheHomeButtonOnce()
    {
        var result = await Time.Drive(Controller.HomeAsync());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, Host.HomeSink.Presses);
        Assert.Equal(["lease:acquired", "home", "lease:disposed"], Host.Journal);
    }
}
