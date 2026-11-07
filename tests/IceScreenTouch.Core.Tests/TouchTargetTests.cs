using System.Net;
using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

// Names of the kind seen on real devices (fictional values): the iPhone's Bluetooth name "example15ぷろ", its mDNS host name
// "example15puro.local", the iPad's "iPad".
public class TouchTargetTests
{
    private static readonly HidClient Phone = new("BluetoothLE#BluetoothLE02:11:22:33:44:55-02:aa:bb:cc:dd:01", "example15ぷろ", 1);
    private static readonly HidClient Pad = new("BluetoothLE#BluetoothLE02:11:22:33:44:55-02:aa:bb:cc:dd:02", "iPad", 2);

    [Fact]
    public void NoDeviceNoTarget()
    {
        Assert.Equal(TargetChoice.None, TouchTargetSelector.Select([], null, null));
    }

    [Fact]
    public void WithoutAnythingElseTheLatestDeviceWins()
    {
        var choice = TouchTargetSelector.Select([Phone, Pad], null, MirrorPeer.Unknown);
        Assert.Equal(new TargetChoice(Pad, TargetReason.Latest), choice);
    }

    [Fact]
    public void ASingleDeviceIsTheTarget()
    {
        Assert.Equal(new TargetChoice(Phone, TargetReason.Latest), TouchTargetSelector.Select([Phone], null, null));
    }

    [Fact]
    public void TheUsersChoiceWinsOverTheMirroredDevice()
    {
        var choice = TouchTargetSelector.Select([Phone, Pad], Phone.Id, new MirrorPeer("iPad", []));
        Assert.Equal(new TargetChoice(Phone, TargetReason.Chosen), choice);
    }

    [Fact]
    public void AChosenDeviceThatIsNotConnectedIsIgnored()
    {
        var choice = TouchTargetSelector.Select([Phone, Pad], "BluetoothLE#gone", new MirrorPeer("example15ぷろ", []));
        Assert.Equal(new TargetChoice(Phone, TargetReason.Mirrored), choice);
    }

    [Fact]
    public void TheDeviceNameFromUxPlayPicksTheMirroredDevice()
    {
        var choice = TouchTargetSelector.Select([Phone, Pad], null, new MirrorPeer("example15ぷろ", []));
        Assert.Equal(new TargetChoice(Phone, TargetReason.Mirrored), choice);
    }

    [Fact]
    public void TheHostNamePicksTheMirroredDevice()
    {
        var choice = TouchTargetSelector.Select([Phone, Pad], null, new MirrorPeer(null, ["example15puro.local"]));
        Assert.Equal(new TargetChoice(Phone, TargetReason.Mirrored), choice);
    }

    [Fact]
    public void AnExactHostNameBeatsAPrefix()
    {
        var pro = new HidClient("BluetoothLE#pro", "iPad Pro", 1);
        var choice = TouchTargetSelector.Select([pro, Pad], null, new MirrorPeer(null, ["iPad.local"]));
        Assert.Equal(Pad, choice.Client);

        choice = TouchTargetSelector.Select([pro, Pad with { Order = 0 }], null, new MirrorPeer(null, ["iPad-Pro.local"]));
        Assert.Equal(pro, choice.Client);
    }

    [Fact]
    public void TwoDevicesOfTheSameNameGoToTheLatest()
    {
        var a = new HidClient("BluetoothLE#a", "iPhone", 1);
        var b = new HidClient("BluetoothLE#b", "iPhone", 2);
        Assert.Equal(new TargetChoice(b, TargetReason.Mirrored), TouchTargetSelector.Select([b, a], null, new MirrorPeer("iPhone", [])));
    }

    [Fact]
    public void AnUnknownNameFallsBackToTheLatest()
    {
        var unnamed = Phone with { Name = null, Order = 3 };
        var choice = TouchTargetSelector.Select([unnamed, Pad], null, new MirrorPeer(null, ["example15puro"]));
        Assert.Equal(new TargetChoice(unnamed, TargetReason.Latest), choice);
    }

    [Fact]
    public void AnUnrelatedHostFallsBackToTheLatest()
    {
        // AirServer also holds Google Cast connections from other machines.
        var choice = TouchTargetSelector.Select([Phone, Pad], null, new MirrorPeer(null, ["Living-Room-TV.local"]));
        Assert.Equal(new TargetChoice(Pad, TargetReason.Latest), choice);
    }

    [Theory]
    [InlineData("example15ぷろ", "example15ぷろ", 2)]
    [InlineData("Alex's iPhone", "alex's iphone ", 2)]
    [InlineData("Alex’s iPhone", "Alex's iPhone", 1)] // a typographic apostrophe
    [InlineData("iPad", "iPhone", 0)]
    [InlineData("たろうのアイフォン", "たろうのアイパッド", 0)]
    [InlineData(null, "iPad", 0)]
    public void NameMatch(string? bluetooth, string device, int expected)
    {
        Assert.Equal(expected, TouchTargetSelector.NameMatch(bluetooth, device));
    }

    [Theory]
    [InlineData("example15ぷろ", "example15puro.local", 1)]
    [InlineData("Alex's iPhone", "Alexs-iPhone.local.", 2)]
    [InlineData("iPad", "iPad", 2)]
    [InlineData("iPad", "iPad-2.local", 1)]
    [InlineData("iPad Pro", "iPad.local", 0)]
    [InlineData("たろうのアイフォン", "tarounoaifon.local", 0)] // no ASCII to go by
    [InlineData("ぷろiP", "puroiP.local", 0)] // too short to trust
    public void HostMatch(string bluetooth, string host, int expected)
    {
        Assert.Equal(expected, TouchTargetSelector.HostMatch(bluetooth, host));
    }

    [Fact]
    public void ReverseNames()
    {
        Assert.Equal("178.1.168.192.in-addr.arpa", MdnsReverseLookup.ReverseName(IPAddress.Parse("192.168.1.178")));
        Assert.Equal(
            "2.a.b.6.2.2.b.4.5.c.e.b.5.4.0.8.0.0.b.7.0.1.0.0.8.b.d.0.1.0.0.2.ip6.arpa",
            MdnsReverseLookup.ReverseName(IPAddress.Parse("2001:db8:10:7b00:8045:bec5:4b22:6ba2")));
    }

    [Fact]
    public void QueryAndAnswer()
    {
        const string name = "178.1.168.192.in-addr.arpa";
        var query = MdnsReverseLookup.BuildQuery(name);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, (byte)'1', (byte)'7', (byte)'8' }, query[..16]);
        Assert.Equal(new byte[] { 0, 0, 12, 0, 1 }, query[^5..]);

        // The iPhone's reply: the question echoed, then the PTR record with a compressed owner name.
        byte[] target = [13, .. "example15puro"u8.ToArray(), 5, .. "local"u8.ToArray(), 0];
        byte[] reply =
        [
            0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            .. query[12..],
            0xC0, 12, 0, 12, 0x80, 1, 0, 0, 0, 10, 0, (byte)target.Length,
            .. target,
        ];
        Assert.Equal("example15puro.local", MdnsReverseLookup.ParsePtrAnswer(reply, name));
        Assert.Null(MdnsReverseLookup.ParsePtrAnswer(reply, "1.1.168.192.in-addr.arpa"));
        Assert.Null(MdnsReverseLookup.ParsePtrAnswer(query, name)); // a query, not a reply
        Assert.Null(MdnsReverseLookup.ParsePtrAnswer(reply.AsSpan(0, 40), name)); // cut short
    }

    [Theory]
    [InlineData("Alexs-iPhone.local", true)]
    [InlineData("example15puro.local", true)]
    [InlineData("iPad-3.local", true)]
    [InlineData("iPhone", true)]
    [InlineData("bad\nname.local", false)]
    [InlineData("bad name.local", false)]
    [InlineData("Alex’s-iPhone.local", false)]
    [InlineData("ぷろ.local", false)]
    [InlineData("a..local", false)]
    [InlineData(".local", false)]
    [InlineData("", false)]
    public void HostNamesAreValidated(string name, bool valid) =>
        Assert.Equal(valid, MdnsReverseLookup.IsValidHostName(name));

    [Fact]
    public void LimitsOfHostNames()
    {
        Assert.True(MdnsReverseLookup.IsValidHostName(new string('a', 63) + ".local"));
        Assert.False(MdnsReverseLookup.IsValidHostName(new string('a', 64) + ".local"));
        Assert.True(MdnsReverseLookup.IsValidHostName(string.Join('.', Enumerable.Repeat(new string('a', 62), 4)))); // 4 * 62 + 3 = 251
        Assert.False(MdnsReverseLookup.IsValidHostName(string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + "a")); // 256
    }

    [Fact]
    public void AnAnswerWithAnUnusableHostNameIsNoAnswer()
    {
        const string name = "178.1.168.192.in-addr.arpa";
        var query = MdnsReverseLookup.BuildQuery(name);
        static byte[] ReplyFor(byte[] query, string label)
        {
            byte[] target = [(byte)label.Length, .. System.Text.Encoding.UTF8.GetBytes(label), 5, .. "local"u8.ToArray(), 0];
            return
            [
                0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0,
                .. query[12..],
                0xC0, 12, 0, 12, 0x80, 1, 0, 0, 0, 10, 0, (byte)target.Length,
                .. target,
            ];
        }

        Assert.Equal("Alexs-iPhone.local", MdnsReverseLookup.ParsePtrAnswer(ReplyFor(query, "Alexs-iPhone"), name));
        Assert.Null(MdnsReverseLookup.ParsePtrAnswer(ReplyFor(query, "evil\n[hid ] fake line"), name)); // newline and space
        Assert.Null(MdnsReverseLookup.ParsePtrAnswer(ReplyFor(query, "ぷろ"), name)); // non-ASCII
        Assert.Null(MdnsReverseLookup.ParsePtrAnswer(ReplyFor(query, new string('a', 63) + "b"), name)); // label of 64
    }
}
