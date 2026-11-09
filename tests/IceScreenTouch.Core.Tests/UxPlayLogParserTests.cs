using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

// Real lines from the PoC 1 session; same checks as poc/poc3-integrated/test_geometry.py.
public class UxPlayLogParserTests
{
    [Fact]
    public void StockSizeLine()
    {
        const string line = "raop_rtp_mirror width_source = 498.000000 height_source = 1080.000000 " +
                            "x_offset = 711.000000 y_offset = 0.000000 width = 498.000000 height = 1080.000000";
        Assert.Equal(new VideoSizeEvent(new SizeD(498, 1080)), UxPlayLogParser.Parse(line));
    }

    [Fact]
    public void DeviceLineKeepsNonAsciiNames()
    {
        const string line = "connection request from Example Phone ぷろ (iPhone16,1) with deviceID = AA:BB:CC:DD:EE:FF";
        Assert.Equal(new DeviceConnectedEvent("Example Phone ぷろ", "iPhone16,1"), UxPlayLogParser.Parse(line));
    }

    [Theory]
    [InlineData("3f a0 12 9c 00 7e 11 22 33 44 55 66 77 88 99 aa", true)] // a key: 16 bytes per line
    [InlineData("be", true)]
    [InlineData("  0a 0B  ", true)]
    [InlineData("16 byte aeskey (fairplay-decrypted from ekey):", false)]
    [InlineData("ekey_len = 16", false)]
    [InlineData("iScrMirror: sink-caps 1170x2532 par 1/1", false)]
    [InlineData("", false)]
    public void HexDumpLinesAreRecognized(string line, bool dump) =>
        Assert.Equal(dump, UxPlayLogParser.IsHexDump(line));

    [Theory]
    [InlineData("deviceID = 02:B2:EF:01:23:45", "deviceID = xx:xx:xx:xx:xx:xx")]
    [InlineData("<string>02:b2:ef:01:23:45</string>", "<string>xx:xx:xx:xx:xx:xx</string>")]
    [InlineData("DACP-ID: 1A2B3C4D5E6F7788", "DACP-ID: (masked)")]
    [InlineData("Active-Remote: 1234567890", "Active-Remote: (masked)")]
    [InlineData("raop_rtp_mirror width_source = 1170.000000", "raop_rtp_mirror width_source = 1170.000000")]
    [InlineData("12:30:45.123 time stamps stay", "12:30:45.123 time stamps stay")]
    public void IdentifiersAreMaskedForTheLogFile(string line, string expected) =>
        Assert.Equal(expected, UxPlayLogParser.Mask(line));

    [Theory]
    [InlineData("21:36:12.327 video_reset: type = RTP_Shutdown")]
    [InlineData("21:36:12.327 video_reset: type = RTP_to_HLS_Shutdown")]
    public void MirroringEnd(string line)
    {
        Assert.Equal(new MirroringEndedEvent(), UxPlayLogParser.Parse(line));
    }

    [Theory]
    [InlineData("21:36:12.327 video_reset: type = NoHold")]
    [InlineData("21:36:12.327 video_reset: type = HLS_Shutdown")]
    [InlineData("21:36:12.327 video_reset: type = on_video_play")]
    [InlineData("21:36:12.327 TEARDOWN request,  96=0, 110=1")]
    [InlineData("21:35:10.574 This packet indicates video stream is stopping")] // a pause, not the end
    public void OtherResetsAreNotTheEndOfMirroring(string line)
    {
        Assert.Null(UxPlayLogParser.Parse(line));
    }

    [Theory]
    [InlineData("Open connections: 0")]
    [InlineData("00:00:11.445 Open connections: 0")]
    [InlineData("  Open connections: 0  ")]
    public void NoConnectionsLeft(string line)
    {
        Assert.Equal(new NoConnectionsEvent(), UxPlayLogParser.Parse(line));
    }

    [Theory]
    [InlineData("Open connections: 1")]
    [InlineData("Open connections: 2")]
    [InlineData("Open connections: 10")]
    [InlineData("Open connections: 00")]
    [InlineData("Open connections: 0x")]
    [InlineData("Open connections: 0 more")]
    [InlineData("Open connections:")]
    public void OtherConnectionCountsAreNotTheEnd(string line)
    {
        Assert.Null(UxPlayLogParser.Parse(line));
    }

    [Fact]
    public void UnrelatedLineIsNull()
    {
        Assert.Null(UxPlayLogParser.Parse("Open connections: 1"));
        Assert.Null(UxPlayLogParser.Parse(""));
    }

    [Theory]
    [InlineData("iScrMirror: sink-caps 498x1080 par 1/1", 498, 1080)]
    [InlineData("iScrMirror: sink-caps 1920x886 par 1/1", 1920, 886)]
    [InlineData("iScrMirror: sink-caps 960x1080 par 2/1", 1920, 1080)] // pixel aspect ratio folded into the width
    public void PatchedSinkSize(string line, double width, double height)
    {
        Assert.Equal(new SinkSizeEvent(new SizeD(width, height)), UxPlayLogParser.Parse(line));
    }

    [Theory]
    [InlineData("iScrMirror: sink-caps 100x100 par 0/1")]
    [InlineData("iScrMirror: sink-caps 100x100 par 1/0")]
    public void InvalidPixelAspectRatioIsIgnored(string line)
    {
        Assert.Null(UxPlayLogParser.Parse(line));
    }

    [Fact]
    public void SinkSizeWinsOverOtherPatternsOnTheSameLine()
    {
        Assert.IsType<SinkSizeEvent>(UxPlayLogParser.Parse("12:00:00 iScrMirror: sink-caps 498x1080 par 1/1 connection request from x (y) with deviceID"));
    }
}
