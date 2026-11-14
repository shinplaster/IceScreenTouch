// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>The touch messages of the stream: down, move, up, coordinates and the 16 ms coalescing.</summary>
public class StreamTouchTests : StreamTestBase
{
    private static TouchContact Contact(int id, AxisPoint p, bool tip = true) => new((byte)id, p.X, p.Y, tip);

    [Fact]
    public async Task DownMoveUpSendsTheFingerAndLiftsAtTheLastPosition()
    {
        var client = await OpenAsync();

        await DownAsync(client, 1, 0.5, 0.7);
        await client.WaitForAckAsync(1);
        Advance(20);
        await MoveAsync(client, 2, 0.5, 0.4);
        await client.WaitForAckAsync(2);
        await UpAsync(client, 3);
        await client.WaitForAckAsync(3);

        var frames = Touch.Frames;
        Assert.Equal(4, frames.Count);
        Assert.Equal([Contact(0, At(0.5, 0.7))], frames[0].Contacts);
        Assert.Equal([Contact(0, At(0.5, 0.4))], frames[1].Contacts);
        Assert.Equal([Contact(0, At(0.5, 0.4), tip: false)], frames[2].Contacts); // the last position, Tip 0
        Assert.True(frames[3].IsEmpty);
        Assert.All(frames.Where(f => !f.IsEmpty), f => Assert.NotEqual(new AxisPoint(0, 0), f.First));
        Assert.True(Controller.IsOperating); // up does not end the session
    }

    [Fact]
    public async Task UpWithoutAFingerIsAnAckAndSendsNothing()
    {
        var client = await OpenAsync();

        await UpAsync(client, 1);

        await client.WaitForAckAsync(1);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task TwoContactsAreSentTogetherAndMoveOnlyTheListedOne()
    {
        var client = await OpenAsync();
        await client.SendAsync("""{ "op": "down", "seq": 1, "contacts": [{ "id": 0, "x": 0.3, "y": 0.5 }, { "id": 1, "x": 0.7, "y": 0.5 }] }""");
        await client.WaitForAckAsync(1);
        Advance(20);

        await MoveAsync(client, 2, 0.8, 0.6, id: 1);
        await client.WaitForAckAsync(2);
        await UpAsync(client, 3);
        await client.WaitForAckAsync(3);

        var frames = Touch.Frames;
        Assert.Equal([Contact(0, At(0.3, 0.5)), Contact(1, At(0.7, 0.5))], frames[0].Contacts);
        Assert.Equal([Contact(0, At(0.3, 0.5)), Contact(1, At(0.8, 0.6))], frames[1].Contacts);
        Assert.Equal([Contact(0, At(0.3, 0.5), false), Contact(1, At(0.8, 0.6), false)], frames[2].Contacts);
        Assert.True(frames[3].IsEmpty);
    }

    [Fact]
    public async Task ADownWhileDownAddsAContactOrReplacesItsPosition()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.3, 0.5);
        await client.WaitForAckAsync(1);

        await DownAsync(client, 2, 0.7, 0.5, id: 1);
        await client.WaitForAckAsync(2);
        await DownAsync(client, 3, 0.4, 0.4, id: 0);
        await client.WaitForAckAsync(3);

        var frames = Touch.Frames;
        Assert.Equal(3, frames.Count); // a down is never held back
        Assert.Equal([Contact(0, At(0.3, 0.5)), Contact(1, At(0.7, 0.5))], frames[1].Contacts);
        Assert.Equal([Contact(0, At(0.4, 0.4)), Contact(1, At(0.7, 0.5))], frames[2].Contacts);
    }

    // ---- Coalescing ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MovesWithinSixteenMillisecondsAreCoalescedIntoTheLatestPosition()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.9);
        await client.WaitForAckAsync(1);

        for (var seq = 2; seq <= 6; seq++)
        {
            await MoveAsync(client, seq, 0.5, 0.9 - (seq * 0.05));
        }

        await BarrierAsync(client);
        Assert.Single(Touch.Frames); // nothing but the down yet
        Advance(15);
        await Task.Delay(50);
        Assert.Single(Touch.Frames);
        Advance(1);

        await WaitUntil(() => Touch.Frames.Count == 2, "the coalesced move");
        await client.WaitForAckAsync(6);
        await Task.Delay(50);
        Assert.Equal(2, Touch.Frames.Count); // exactly one report for five moves
        Assert.Equal([Contact(0, At(0.5, 0.6))], Touch.Frames[1].Contacts); // the latest one
        Assert.Equal(Ms(16), Touch.Frames[1].At - Touch.Frames[0].At);
        for (var seq = 2; seq <= 6; seq++)
        {
            // Every move is answered exactly once.
            Assert.Equal(1, client.Events.Count(e => StreamClient.IsEvent(e, "ack") && e.GetProperty("seq").GetInt32() == seq));
        }
    }

    [Fact]
    public async Task NeverMoreThanOneMoveReportPerSixteenMilliseconds()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        var seq = 2;

        for (var round = 1; round <= 5; round++)
        {
            await MoveAsync(client, seq++, 0.5, 0.5 + (round * 0.01));
            await MoveAsync(client, seq++, 0.5, 0.5 + (round * 0.02));
            await BarrierAsync(client);
            Advance(16);
            await WaitUntil(() => Touch.Frames.Count == 1 + round, $"the report of round {round}");
        }

        var frames = Touch.Frames;
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.True(frames[i].At - frames[i - 1].At >= Ms(16));
        }

        Assert.Equal([Contact(0, At(0.5, 0.6))], frames[^1].Contacts);
    }

    [Fact]
    public async Task AMoveAfterTheIntervalGoesOutAtOnce()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        Advance(16);

        await MoveAsync(client, 2, 0.5, 0.6);

        await client.WaitForAckAsync(2);
        Assert.Equal(2, Touch.Frames.Count);
    }

    [Fact]
    public async Task UpLiftsAtThePositionOfAMoveThatWasNotSentYet()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        await MoveAsync(client, 2, 0.2, 0.3); // held back (the same instant as the down)
        await BarrierAsync(client);
        Assert.Single(Touch.Frames);

        await UpAsync(client, 3);

        await client.WaitForAckAsync(3);
        await client.WaitForAckAsync(2);
        var frames = Touch.Frames;
        Assert.Equal(3, frames.Count); // the down, the lift, the empty report: the held move is not sent on its own
        Assert.Equal([Contact(0, At(0.2, 0.3), tip: false)], frames[1].Contacts);
        Assert.True(frames[2].IsEmpty);
        Advance(100);
        await Task.Delay(50);
        Assert.Equal(3, Touch.Frames.Count); // the flush timer finds nothing to send
    }

    // ---- Validation ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMoveBeforeAnyDownIsAnErrorAndSendsNothing()
    {
        var client = await OpenAsync();

        await MoveAsync(client, 4, 0.5, 0.5);

        var error = await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Equal(4, error.GetProperty("seq").GetInt32());
        Assert.Empty(Touch.Frames);
        await BarrierAsync(client);
        Assert.False(client.Ended);
    }

    [Fact]
    public async Task AMoveAfterUpIsAnError()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await UpAsync(client, 2);
        await client.WaitForAckAsync(2);
        Advance(20);

        await MoveAsync(client, 3, 0.5, 0.5);

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Equal(3, Touch.Frames.Count);
    }

    [Theory]
    [InlineData("""[{ "id": 2, "x": 0.5, "y": 0.5 }]""")]
    [InlineData("""[{ "id": -1, "x": 0.5, "y": 0.5 }]""")]
    [InlineData("""[{ "id": 0, "x": 0.5, "y": 0.5 }, { "id": 0, "x": 0.6, "y": 0.6 }]""")]
    [InlineData("""[{ "id": 0, "x": 0.1, "y": 0.1 }, { "id": 1, "x": 0.2, "y": 0.2 }, { "id": 2, "x": 0.3, "y": 0.3 }]""")]
    [InlineData("[]")]
    [InlineData("""[{ "id": 0, "x": 1.01, "y": 0.5 }]""")]
    [InlineData("""[{ "id": 0, "x": 0.5, "y": -0.1 }]""")]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task InvalidContactsAreRefusedWithTheSeqAndNothingIsSent(string contacts)
    {
        var client = await OpenAsync();

        await client.SendAsync($$"""{ "op": "down", "seq": 9, "contacts": {{contacts}} }""");

        var error = await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Equal(9, error.GetProperty("seq").GetInt32());
        Assert.Empty(Touch.Frames);
        await BarrierAsync(client);
        Assert.False(client.Ended);
    }

    [Fact]
    public async Task AMoveOfAContactThatIsNotDownIsAnErrorAndMovesNothing()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        Advance(20);

        await MoveAsync(client, 2, 0.6, 0.6, id: 1);

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Single(Touch.Frames);
    }

    [Fact]
    public async Task AFailedMoveKeepsTheFingerWhereItWas()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        Advance(20);
        await MoveAsync(client, 2, 1.5, 0.5); // out of range
        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);

        await UpAsync(client, 3);

        await client.WaitForAckAsync(3);
        Assert.Equal([Contact(0, At(0.5, 0.5), tip: false)], Touch.Frames[1].Contacts);
    }

    [Fact]
    public async Task ADownThatFailsLeavesEarlierFingersAlone()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        await DownAsync(client, 2, 2.0, 0.5, id: 1);

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        await UpAsync(client, 3);
        await client.WaitForAckAsync(3);
        Assert.Equal([Contact(0, At(0.5, 0.5), tip: false)], Touch.Frames[1].Contacts);
    }

    [Fact]
    public async Task ADownNeedsASubscribedDevice()
    {
        var client = await OpenAsync();
        Touch.IsSubscribed = false;

        await DownAsync(client, 1, 0.5, 0.5);

        await client.WaitForErrorAsync(ApiErrorCodes.NotConnected);
        Assert.Empty(Touch.Frames);
    }

    // ---- Coordinate spaces --------------------------------------------------------------------------------------------

    [Fact]
    public async Task PixelPositionsUseTheRefOfTheDown()
    {
        var client = await OpenAsync();

        await client.SendAsync("""{ "op": "down", "seq": 1, "space": "pixel", "ref": { "width": 390, "height": 844 }, "contacts": [{ "id": 0, "x": 195, "y": 422 }] }""");
        await client.WaitForAckAsync(1);
        Advance(20);
        await client.SendAsync("""{ "op": "move", "seq": 2, "contacts": [{ "id": 0, "x": 390, "y": 844 }] }""");
        await client.WaitForAckAsync(2);

        Assert.Equal([Contact(0, At(0.5, 0.5))], Touch.Frames[0].Contacts);
        Assert.Equal([Contact(0, At(1, 1))], Touch.Frames[1].Contacts);
    }

    [Fact]
    public async Task APixelPositionOutsideTheRefIsInvalid()
    {
        var client = await OpenAsync();

        await client.SendAsync("""{ "op": "down", "seq": 1, "space": "pixel", "ref": { "width": 390, "height": 844 }, "contacts": [{ "id": 0, "x": 391, "y": 10 }] }""");

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
    }

    [Theory]
    [InlineData("""{ "op": "down", "seq": 1, "space": "pixel", "contacts": [{ "id": 0, "x": 5, "y": 5 }] }""")]
    [InlineData("""{ "op": "down", "seq": 1, "space": "pixel", "ref": { "width": 0, "height": 844 }, "contacts": [{ "id": 0, "x": 0, "y": 0 }] }""")]
    [InlineData("""{ "op": "down", "seq": 1, "space": "planet", "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""")]
    public async Task ABadSpaceOrRefIsInvalid(string message)
    {
        var client = await OpenAsync();

        await client.SendAsync(message);

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task AScreenshotOfAnotherOrientationIsPictureChanged()
    {
        var client = await OpenAsync(); // a portrait picture

        await client.SendAsync("""{ "op": "down", "seq": 1, "space": "pixel", "ref": { "width": 844, "height": 390 }, "contacts": [{ "id": 0, "x": 100, "y": 100 }] }""");

        var error = await client.WaitForErrorAsync(ApiErrorCodes.PictureChanged);
        Assert.Equal(1, error.GetProperty("seq").GetInt32());
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task APictureThatTurnsDuringTheGestureMakesThatMoveAnErrorAndTheFingerStays()
    {
        var client = await OpenAsync();
        await client.SendAsync("""{ "op": "down", "seq": 1, "space": "pixel", "ref": { "width": 390, "height": 844 }, "contacts": [{ "id": 0, "x": 78, "y": 422 }] }""");
        await client.WaitForAckAsync(1);
        Host.Picture = new PictureInfo(844, 390); // the phone turned
        Advance(20);

        await client.SendAsync("""{ "op": "move", "seq": 2, "contacts": [{ "id": 0, "x": 78, "y": 422 }] }""");

        await client.WaitForErrorAsync(ApiErrorCodes.PictureChanged); // the ref is of the other orientation
        Assert.Single(Touch.Frames);
        await UpAsync(client, 3);
        await client.WaitForAckAsync(3);
        Assert.Equal([Contact(0, At(0.2, 0.5), tip: false)], Touch.Frames[1].Contacts);
    }

    [Fact]
    public async Task ARotationDuringTheGestureIsHonouredByTheNextMove()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.2, 0.5);
        await client.WaitForAckAsync(1);
        Host.Picture = new PictureInfo(844, 390);
        Advance(20);

        await MoveAsync(client, 2, 0.2, 0.5);

        await client.WaitForAckAsync(2);
        Assert.Equal([Contact(0, At(0.2, 0.5))], Touch.Frames[0].Contacts);
        Assert.Equal([Contact(0, Expected(0.2, 0.5, Rotation.R90))], Touch.Frames[1].Contacts);
    }

    [Fact]
    public async Task NoPictureIsAnErrorForScreenAndPixelButNotForDevice()
    {
        var client = await OpenAsync();
        Host.Picture = null;

        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForErrorAsync(ApiErrorCodes.NoPicture);
        Assert.Empty(Touch.Frames);

        await client.SendAsync("""{ "op": "down", "seq": 2, "space": "device", "contacts": [{ "id": 0, "x": 0.25, "y": 0.75 }] }""");
        await client.WaitForAckAsync(2);
        Assert.Equal([Contact(0, Geometry.UnitToAxis(new PointD(0.25, 0.75)))], Touch.Frames[0].Contacts);
    }

    [Fact]
    public async Task ALandscapePictureRotatesScreenPositions()
    {
        Host.Picture = new PictureInfo(844, 390);
        var client = await OpenAsync();

        await DownAsync(client, 1, 0.2, 0.4);
        await client.WaitForAckAsync(1);

        Assert.Equal([Contact(0, Expected(0.2, 0.4, Rotation.R90))], Touch.Frames[0].Contacts);
    }
}
