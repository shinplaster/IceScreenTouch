using IceScreenTouch.Bridge;
using IceScreenTouch.Core;
using IceScreenTouch.Video;

namespace IceScreenTouch.Windows.Tests;

public sealed class UxPlayVideoSourceTests
{
    private static readonly string[] ExpectedArguments =
        ["-n", "Phone", "-nh", "-vs", "d3d11videosink", "-vsync", "no", "-d", "-vd", "d3d11h264dec compliance=flexible", "-vc", "d3d11convert", "-FPSdata"];

    [Fact]
    public void TheArgumentsAreThoseOfPoC3WithTheLowLatencyDecoder()
    {
        var o = new UxPlayOptions { ExePath = "uxplay.exe", Name = "Phone", ExtraArguments = ["-FPSdata"] };

        Assert.Equal(ExpectedArguments, o.BuildArguments());
    }

    [Fact]
    public void TheDecoderFollowsTheSink()
    {
        var o = new UxPlayOptions { ExePath = "uxplay.exe", Name = "Phone" };

        Assert.Equal(
            ["-n", "Phone", "-nh", "-vs", "d3d12videosink", "-vsync", "no", "-d", "-vd", "d3d12h264dec compliance=flexible", "-vc", "d3d12convert"],
            o.WithSink("d3d12videosink").BuildArguments());
        Assert.Equal(["-n", "Phone", "-nh", "-vs", "autovideosink", "-vsync", "no", "-d"], o.WithSink("autovideosink").BuildArguments());
    }

    [Fact]
    public void TheSinkSizeWinsOverTheStockLogLine()
    {
        var logged = new List<string>();
        var ux = new UxPlayVideoSource(logged.Add, verbose: false);

        ux.OnLine("raop_rtp_mirror width_source = 1170.000000 height_source = 2532.000000 x_offset = 0.000000 y_offset = 0.000000 width = 498.000000 height = 1080.000000");
        Assert.Equal(new SizeD(498, 1080), ux.VideoSize);
        Assert.Equal("log", ux.SizeSource);

        ux.OnLine("iScrMirror: sink-caps 1920x886 par 1/1");
        ux.OnLine("raop_rtp_mirror width_source = 1.000000 height_source = 1.000000 x_offset = 0.000000 y_offset = 0.000000 width = 10.000000 height = 20.000000");

        Assert.Equal(new SizeD(1920, 886), ux.VideoSize);
        Assert.Equal("sink", ux.SizeSource);
        Assert.Equal(2, ux.CodecPackets);
        Assert.Contains("[video] sink size 1920x886", logged);
    }

    [Fact]
    public void TheConnectedPhoneIsRemembered()
    {
        var ux = new UxPlayVideoSource(null, verbose: false);

        ux.OnLine("connection request from iPhone (iPhone16,1) with deviceID = 12:34");

        Assert.Equal("iPhone", ux.DeviceName);
        Assert.Equal("iPhone16,1", ux.DeviceModel);
    }

    [Fact]
    public void WhenMirroringEndsTheSizeAndThePhoneAreForgotten()
    {
        var logged = new List<string>();
        var ux = new UxPlayVideoSource(logged.Add, verbose: false);
        ux.OnLine("connection request from Alex's iPhone (iPhone16,1) with deviceID = 12:34");
        ux.OnLine("raop_rtp_mirror width_source = 498.000000 height_source = 1080.000000 x_offset = 711.000000 y_offset = 0.000000 width = 498.000000 height = 1080.000000");
        ux.OnLine("iScrMirror: sink-caps 498x1080 par 1/1");
        Assert.Equal(new SizeD(498, 1080), ux.VideoSize);

        // The video TEARDOWN and the final one each print it: the end is logged once.
        ux.OnLine("video_reset: type = RTP_Shutdown");
        ux.OnLine("video_reset: type = RTP_Shutdown");

        Assert.Null(ux.VideoSize);
        Assert.Null(ux.SinkSize);
        Assert.Equal("-", ux.SizeSource);
        Assert.Null(ux.DeviceName);
        Assert.Null(ux.DeviceModel);
        Assert.Single(logged, "[video] mirroring ended");

        // The next session starts from scratch.
        ux.OnLine("connection request from Sam's iPad (iPad13,1) with deviceID = 56:78");
        ux.OnLine("iScrMirror: sink-caps 1080x810 par 1/1");
        Assert.Equal("Sam's iPad", ux.DeviceName);
        Assert.Equal(new SizeD(1080, 810), ux.VideoSize);
    }

    [Fact]
    public void APauseOrAnAudioTeardownKeepsTheSession()
    {
        var ux = new UxPlayVideoSource(null, verbose: false);
        ux.OnLine("connection request from Alex's iPhone (iPhone16,1) with deviceID = 12:34");
        ux.OnLine("iScrMirror: sink-caps 498x1080 par 1/1");

        ux.OnLine("This packet indicates video stream is stopping");
        ux.OnLine("TEARDOWN request,  96=1, 110=0");

        Assert.Equal(new SizeD(498, 1080), ux.VideoSize);
        Assert.Equal("Alex's iPhone", ux.DeviceName);
    }

    [Fact]
    public void TheTailKeepsTheLastSixtyLines()
    {
        var ux = new UxPlayVideoSource(null, verbose: false);
        for (var i = 0; i < 100; i++)
        {
            ux.OnLine($"line {i}");
        }

        var tail = ux.Tail();
        Assert.Equal(60, tail.Count);
        Assert.Equal("line 99", tail[^1]);
    }

    [Fact]
    public void WithoutAProcessThereIsNoWindow()
    {
        var ux = new UxPlayVideoSource(null, verbose: false);

        Assert.False(ux.IsRunning);
        Assert.Equal(0, ux.FindWindow());
        Assert.False(ux.Mark("x")); // no log file
    }

    [Fact]
    public void StartingAMissingExeFails()
    {
        Assert.Throws<FileNotFoundException>(() => UxPlayVideoSource.Start(new UxPlayOptions { ExePath = @"C:\nonexistent\uxplay.exe" }));
    }

    [Fact]
    public void TheLocalBuildIsFoundFromASubfolderAndThePatchedOneComesFirst()
    {
        var root = Directory.CreateTempSubdirectory("iscr-");
        try
        {
            var ux = Path.Combine(root.FullName, "poc", "poc1-uxplay", "UxPlay");
            Directory.CreateDirectory(Path.Combine(ux, "build"));
            File.WriteAllText(Path.Combine(ux, "build", "uxplay.exe"), string.Empty);
            var deep = Directory.CreateDirectory(Path.Combine(root.FullName, "tools", "x", "bin")).FullName;

            // Not the repository (no marker): nothing is taken from there.
            Assert.Null(UxPlayOptions.FindLocalBuild(deep));

            File.WriteAllText(Path.Combine(root.FullName, UxPlayOptions.RepositoryMarker), string.Empty);
            Assert.Equal(Path.Combine(ux, "build", "uxplay.exe"), UxPlayOptions.FindLocalBuild(deep));

            Directory.CreateDirectory(Path.Combine(ux, "build-isr"));
            File.WriteAllText(Path.Combine(ux, "build-isr", "uxplay.exe"), string.Empty);
            Assert.Equal(Path.Combine(ux, "build-isr", "uxplay.exe"), UxPlayOptions.FindLocalBuild(deep));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheCommandLineIsParsed()
    {
        var s = BridgeCommandLine.Parse(["--name", "X", "--uxplay-arg", "-s", "--uxplay-arg", "1920x1080", "--landscape-rot", "r270", "--no-follow", "--dry-run", "-v", "--collect", "--duration", "2.5"]);

        Assert.Equal("X", s.Name);
        Assert.Equal(["-s", "1920x1080", "-FPSdata"], s.UxPlayArgs);
        Assert.Equal(Rotation.R270, s.LandscapeRotation);
        Assert.False(s.Follow);
        Assert.True(s.DryRun && s.Verbose && s.Collect);
        Assert.Equal(2.5, s.DurationSeconds);
        Assert.Null(s.Attach);
    }

    [Fact]
    public void AttachTakesAnOptionalProcessName()
    {
        Assert.Equal("uxplay", BridgeCommandLine.Parse(["--attach", "-v"]).Attach);
        Assert.Equal("AirServer", BridgeCommandLine.Parse(["--attach", "AirServer"]).Attach);
    }

    [Theory]
    [InlineData("45")]
    [InlineData("r")]
    [InlineData("--x")]
    public void BadInputIsRejected(string rotation)
    {
        Assert.Throws<ArgumentException>(() => BridgeCommandLine.Parse(["--landscape-rot", rotation]));
    }

    [Fact]
    public void UnknownOptionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => BridgeCommandLine.Parse(["--bogus"]));
    }
}
