// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>The key messages of the stream: held keys, modifiers and the release reports.</summary>
public class StreamKeyTests : StreamTestBase
{
    private const byte A = 0x04;
    private const byte B = 0x05;

    private static string R(byte modifiers, params byte[] keys) => $"{modifiers:X2}|{string.Join(',', keys.Select(k => k.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))}";

    private List<string> Reports() => [.. Keys.Reports.Select(r => R(r.Modifiers, r.Keys))];

    [Fact]
    public async Task AKeyGoesDownAndUp()
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, "a", down: true);
        await client.WaitForAckAsync(1);
        await KeyAsync(client, 2, "a", down: false);
        await client.WaitForAckAsync(2);

        Assert.Equal([R(0, A), R(0)], Reports());
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task AModifierGoesDownBeforeTheKeyAndComesUpAfterIt()
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, "a", down: true, modifiers: "\"cmd\"");
        await client.WaitForAckAsync(1);
        await KeyAsync(client, 2, "a", down: false, modifiers: "\"cmd\"");
        await client.WaitForAckAsync(2);

        Assert.Equal(
            [
                R(HidModifiers.LeftGui),
                R(HidModifiers.LeftGui, A),
                R(HidModifiers.LeftGui),
                R(0),
            ],
            Reports());
    }

    [Fact]
    public async Task SeveralModifiersAreCombined()
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, "Space", down: true, modifiers: "\"ctrl\", \"shift\"");
        await client.WaitForAckAsync(1);

        Assert.Equal(R((byte)(HidModifiers.LeftControl | HidModifiers.LeftShift), 0x2C), Reports()[^1]); // Space
    }

    [Fact]
    public async Task HeldKeysAreTrackedAndReleasedOneByOne()
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, "a", down: true);
        await KeyAsync(client, 2, "b", down: true);
        await KeyAsync(client, 3, "a", down: false);
        await KeyAsync(client, 4, "b", down: false);
        await client.WaitForAckAsync(4);

        Assert.Equal(
            [
                R(0, A),
                R(0, A, B),
                R(0, B),
                R(0),
            ],
            Reports());
    }

    [Fact]
    public async Task ReleasingAKeyKeepsTheModifierWhileAnotherHeldKeyStillNeedsIt()
    {
        var client = await OpenAsync();
        await KeyAsync(client, 1, "a", down: true, modifiers: "\"cmd\"");
        await KeyAsync(client, 2, "b", down: true, modifiers: "\"cmd\"");

        await KeyAsync(client, 3, "a", down: false);
        await client.WaitForAckAsync(3);

        Assert.Equal(
            [
                R(HidModifiers.LeftGui),
                R(HidModifiers.LeftGui, A),
                R(HidModifiers.LeftGui, A, B), // the modifier was already down: no separate report
                R(HidModifiers.LeftGui, B), // a is up, cmd stays for b
            ],
            Reports());

        await KeyAsync(client, 4, "b", down: false);
        await client.WaitForAckAsync(4);

        Assert.Equal([R(HidModifiers.LeftGui), R(0)], Reports()[^2..]);
    }

    [Fact]
    public async Task AKeyThatIsAlreadyDownSendsNothingNew()
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, "a", down: true);
        await KeyAsync(client, 2, "a", down: true);
        await client.WaitForAckAsync(2);

        Assert.Single(Keys.Reports);
    }

    [Fact]
    public async Task ReleasingAKeyThatIsNotDownIsAnAckAndSendsNothing()
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, "a", down: false);

        await client.WaitForAckAsync(1);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task AtMostSixKeysCanBeDown()
    {
        var client = await OpenAsync();
        var seq = 1;
        foreach (var key in new[] { "a", "b", "c", "d", "e", "f" })
        {
            await KeyAsync(client, seq++, key, down: true);
        }

        await client.WaitForAckAsync(6);
        await KeyAsync(client, 7, "g", down: true);

        var error = await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Equal(7, error.GetProperty("seq").GetInt32());
        Assert.Equal(6, Keys.Reports.Count);
        Assert.Equal(6, Keys.Reports[^1].Keys.Length);
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("A")]
    [InlineData("!")]
    [InlineData("F13")]
    [InlineData("")]
    public async Task AnUnknownKeyIsAnErrorAndSendsNothing(string key)
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, key, down: true);

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
        await BarrierAsync(client);
        Assert.False(client.Ended);
    }

    [Fact]
    public async Task AnUnknownModifierIsAnError()
    {
        var client = await OpenAsync();

        await KeyAsync(client, 1, "a", down: true, modifiers: "\"hyper\"");

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task KeyNeedsItsDownFlag()
    {
        var client = await OpenAsync();

        await client.SendAsync("""{ "op": "key", "seq": 1, "key": "a" }""");

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task WithoutAKeyboardOnTheHidMapTheKeyIsNoKeyboard()
    {
        Keys.HasKeyboard = false;
        var client = await OpenAsync();

        await KeyAsync(client, 1, "a", down: true);

        var error = await client.WaitForErrorAsync(ApiErrorCodes.NoKeyboard);
        Assert.Equal(1, error.GetProperty("seq").GetInt32());
        Assert.Empty(Keys.Reports);
        await BarrierAsync(client);
        Assert.False(client.Ended); // touch still works
        await DownAsync(client, 2, 0.5, 0.5);
        await client.WaitForAckAsync(2);
    }

    [Fact]
    public async Task KeysAndFingersWorkTogether()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await KeyAsync(client, 2, "a", down: true, modifiers: "\"shift\"");
        await client.WaitForAckAsync(2);

        await UpAsync(client, 3);
        await client.WaitForAckAsync(3);

        Assert.Equal(3, Touch.Frames.Count); // the key is still down
        Assert.Equal(R(HidModifiers.LeftShift, A), Reports()[^1]);
    }

    [Fact]
    public async Task ClosingReleasesTheKeysBeforeTheInputGoesBack()
    {
        var client = await OpenAsync();
        await KeyAsync(client, 1, "a", down: true, modifiers: "\"cmd\"");
        await client.WaitForAckAsync(1);

        await client.CloseAsync();

        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        Assert.True(Keys.Reports[^1].IsRelease);
        Assert.True(Host.Journal.IndexOf("key:release") < Host.Journal.IndexOf("lease:disposed"));
    }
}
